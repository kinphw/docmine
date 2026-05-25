// HwpxZipReader — HWPX(ZIP) 아카이브 직접 파싱. DRM 없는 파일 전용.
// Python hwp_parser.py 의 ZipDocReader 1:1 포팅.
//
// DRM 보호 파일은 ZIP 시그니처(PK\x03\x04) 자리에 한컴 자체 암호화 헤더가 있어
// 직접 열기 불가 — HwpxDrmError 던지고 호출자가 COM 백엔드(HwpWorker) 로 fallback.

using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DocMine.Core.Hwp;

public sealed class HwpxZipReader
{
    private static readonly Regex SectionFileRe = new(
        @"^(Contents/)?[Ss]ection(\d+)\.xml$",
        RegexOptions.Compiled);

    private static readonly string[] ContentHpfPaths = { "Contents/content.hpf", "content.hpf" };

    private static readonly HashSet<string> MetaKeys = new(StringComparer.Ordinal)
    {
        "title", "creator", "description", "subject", "language"
    };

    /// <summary>
    /// 파일이 DRM 암호화된 ZIP 인지 빠르게 판별 — ZIP 시그니처 'PK' prefix 확인.
    /// </summary>
    public static bool IsDrmProtected(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            var n = fs.Read(header);
            if (n < 2) return false;
            return !(header[0] == (byte)'P' && header[1] == (byte)'K');
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>HWPX 아카이브를 열어 HwpxDocument 반환. DRM 또는 ZIP 실패 시 HwpxDrmError.</summary>
    public HwpxDocument ReadDocument(string path, SectionParser sectionParser)
    {
        var suffix = Path.GetExtension(path).ToLowerInvariant();
        if (suffix is not (".hwpx" or ".hwp"))
            throw new HwpxFormatError(
                $"{Path.GetFileName(path)}: 지원하지 않는 형식 ({suffix}). .hwpx 또는 .hwp 만.");

        if (IsDrmProtected(path))
            throw new HwpxDrmError(
                $"{Path.GetFileName(path)}: DRM 보호 파일 — COM 백엔드 필요.");

        ZipArchive zf;
        try
        {
            zf = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new HwpxDrmError(
                $"{Path.GetFileName(path)}: ZIP 열기 실패 — DRM 보호 가능성.", ex);
        }

        using (zf)
        {
            var entries = SectionEntries(zf);
            if (entries.Count == 0)
                throw new HwpxFormatError(
                    $"{Path.GetFileName(path)}: 섹션 파일이 없습니다.");

            var metadata = ReadMetadata(zf);
            var sections = new List<Section>(entries.Count);
            foreach (var (idx, entryName) in entries)
            {
                var entry = zf.GetEntry(entryName);
                if (entry is null) continue;
                using var es = entry.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                sections.Add(sectionParser.ParseXml(ms.ToArray(), idx));
            }

            return new HwpxDocument
            {
                Path = path,
                Sections = sections,
                Metadata = metadata,
            };
        }
    }

    private static List<(int Index, string Name)> SectionEntries(ZipArchive zf)
    {
        var result = new List<(int Index, string Name)>();
        foreach (var entry in zf.Entries)
        {
            var m = SectionFileRe.Match(entry.FullName);
            if (m.Success && int.TryParse(m.Groups[2].Value, out var idx))
                result.Add((idx, entry.FullName));
        }
        result.Sort((a, b) => a.Index.CompareTo(b.Index));
        return result;
    }

    private static Dictionary<string, string> ReadMetadata(ZipArchive zf)
    {
        var meta = new Dictionary<string, string>();
        foreach (var hpfPath in ContentHpfPaths)
        {
            var entry = zf.GetEntry(hpfPath);
            if (entry is null) continue;
            try
            {
                using var es = entry.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                var doc = XDocument.Load(new MemoryStream(ms.ToArray()));
                foreach (var node in doc.Descendants())
                {
                    var local = node.Name.LocalName;
                    if (MetaKeys.Contains(local) && !string.IsNullOrWhiteSpace(node.Value))
                        meta[local] = node.Value.Trim();
                }
            }
            catch (System.Xml.XmlException) { }
            catch (IOException) { }
            break;
        }
        return meta;
    }
}
