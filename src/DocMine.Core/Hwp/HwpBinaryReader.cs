// HwpBinaryReader — 바이너리 .hwp(HWP5) 직접 파싱. COM/한글 무의존.
//
// HWP5 = 공개 표준의 조합이라 외부 라이브러리 없이 직접 구현 가능:
//   ① OLE 복합문서(CFB, MS 공개)         → 컨테이너. 아래 Cfb 리더로 스트림 접근.
//   ② raw deflate(zlib, .NET 내장)        → 섹션 압축. DeflateStream 으로 해제.
//   ③ HWP5 레코드(한컴 공개 스펙)          → BodyText/SectionN 안의 PARA_TEXT 추출.
//
// HwpxZipReader(.hwpx ZIP 직접 파싱) 와 동격 — 결과를 같은 HwpxDocument 모델로 흘려보내
// 후처리(ExtractText)를 HWPX 경로와 일원화한다.
//
// 폴백 신호(호출자가 COM 백엔드로 전환):
//   - CFB 서명 아님(구형 HWP3 / DRM 암호문) → HwpxDrmError
//   - 배포용(암호화) 문서                    → HwpxDrmError
//   - FileHeader 없음 / 섹션 없음            → HwpxFormatError
//   - 섹션 압축 해제 실패                    → HwpxParseError
// 내용 read 는 DRM 불변식상 워커 프로세스(python.exe)에서만 — 이 클래스도 워커에서 호출된다.

using System.IO.Compression;

namespace DocMine.Core.Hwp;

public sealed class HwpBinaryReader
{
    /// <summary>바이너리 .hwp 를 열어 HwpxDocument(섹션별 문단) 반환. 실패 시 HwpxError 계열.</summary>
    public HwpxDocument ReadDocument(string path)
    {
        var suffix = Path.GetExtension(path).ToLowerInvariant();
        if (suffix != ".hwp")
            throw new HwpxFormatError($"{Path.GetFileName(path)}: HwpBinaryReader 는 .hwp 전용 ({suffix}).");

        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch (Exception ex) { throw new HwpxFormatError($"{Path.GetFileName(path)}: 파일 읽기 실패 — {ex.Message}"); }

        Cfb cfb;
        try { cfb = new Cfb(raw); }
        catch (InvalidDataException)
        {
            // CFB 서명 아님 = 구형 HWP3 이거나 DRM 복호화 안 된 암호문 → COM 백엔드로.
            throw new HwpxDrmError(
                $"{Path.GetFileName(path)}: HWP5(CFB) 형식 아님 — 구형 HWP3 또는 DRM 암호문 가능. COM 백엔드 필요.");
        }

        var fh = cfb.ReadStreamByName("FileHeader");
        if (fh is null || fh.Length < 40)
            throw new HwpxFormatError($"{Path.GetFileName(path)}: FileHeader 없음/짧음 — HWP5 아님.");

        uint flags = BitConverter.ToUInt32(fh, 36);
        bool compressed   = (flags & 0x01) != 0;
        bool encrypted    = (flags & 0x02) != 0;
        bool distribution = (flags & 0x04) != 0;   // 배포용 — 섹션 추가 암호화

        if (encrypted || distribution)
            throw new HwpxDrmError(
                $"{Path.GetFileName(path)}: 배포용/암호화 문서 — 매니지드 파싱 불가, COM 백엔드 필요.");

        var doc = new HwpxDocument { Path = path };
        foreach (var (name, data) in cfb.SectionStreams())
        {
            byte[] body;
            try { body = compressed ? Inflate(data) : data; }
            catch (Exception ex) { throw new HwpxParseError($"{Path.GetFileName(path)}: 섹션 {name} 압축 해제 실패", ex); }

            var section = new Section { Index = SecIndex(name) };
            ExtractParagraphs(body, section);
            doc.Sections.Add(section);
        }

        if (doc.Sections.Count == 0)
            throw new HwpxFormatError($"{Path.GetFileName(path)}: BodyText 섹션이 없습니다.");

        return doc;
    }

    private static int SecIndex(string name)
        => int.TryParse(name.AsSpan("Section".Length), out var v) ? v : 0;

    // raw deflate (Python zlib.decompress(data, -15) 등가 — zlib 헤더 없는 순수 deflate)
    private static byte[] Inflate(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream(data.Length * 3);
        ds.CopyTo(outMs);
        return outMs.ToArray();
    }

    // HWP5 레코드 스트림 순회 — PARA_TEXT(tag=67) 하나 = 문단 하나로 매핑.
    //   레코드 헤더(UInt32 LE): tag(10bit) | level(10bit) | size(12bit).
    //   size == 0xFFF 면 다음 UInt32 가 실제 크기(확장 헤더).
    private static void ExtractParagraphs(byte[] buf, Section section)
    {
        const int HWPTAG_PARA_TEXT = 67;
        int pos = 0;
        while (pos + 4 <= buf.Length)
        {
            uint header = BitConverter.ToUInt32(buf, pos); pos += 4;
            int tag  = (int)(header & 0x3FF);
            int size = (int)((header >> 20) & 0xFFF);
            if (size == 0xFFF)
            {
                if (pos + 4 > buf.Length) break;
                size = (int)BitConverter.ToUInt32(buf, pos); pos += 4;
            }
            if (size < 0 || pos + size > buf.Length) break;

            if (tag == HWPTAG_PARA_TEXT)
            {
                var text = DecodeParaText(buf, pos, size);
                if (text.Length > 0)
                    section.Blocks.Add(new ParagraphBlock(new Paragraph { Runs = { new TextRun(text) } }));
            }
            pos += size;
        }
    }

    // PARA_TEXT payload: UTF-16LE wchar 열 + 인라인 제어문자.
    //   일반문자(>=32): 그대로 · 10/13: 줄바꿈 · char control(0,24~31): 1 wchar 스킵
    //   inline/extended control(그 외 1~31): 8 wchar(16B) 스킵
    private static string DecodeParaText(byte[] buf, int start, int size)
    {
        var sb = new System.Text.StringBuilder(size / 2);
        int end = start + size;
        int i = start;
        while (i + 2 <= end)
        {
            ushort c = BitConverter.ToUInt16(buf, i);
            if (c >= 32)                       { sb.Append((char)c); i += 2; }
            else if (c == 10 || c == 13)       { sb.Append('\n');    i += 2; }
            else if (c == 0 || (c >= 24 && c <= 31)) { i += 2; }   // char control
            else                               { i += 16; }         // inline/extended control (8 wchar)
        }
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────
    // CFB (Compound File Binary / OLE2) 최소 리더 — FAT + mini-FAT 지원.
    // 대상 .hwp 는 작아(<7MB) DIFAT 가 헤더 내 109 엔트리에 다 들어간다고 가정.
    private sealed class Cfb
    {
        const uint ENDOFCHAIN = 0xFFFFFFFE;
        readonly byte[] _d;
        readonly int _secSize, _miniSecSize, _miniCutoff;
        readonly uint[] _fat;
        readonly uint[] _miniFat;
        readonly byte[] _miniStream;
        readonly List<DirEntry> _dir = new();

        public Cfb(byte[] data)
        {
            _d = data;
            if (data.Length < 512 || BitConverter.ToUInt64(_d, 0) != 0xE11AB1A1E011CFD0)
                throw new InvalidDataException("CFB 서명 아님 (OLE 복합문서가 아님)");
            _secSize     = 1 << BitConverter.ToUInt16(_d, 30);   // 보통 512
            _miniSecSize = 1 << BitConverter.ToUInt16(_d, 32);   // 보통 64
            uint numFatSectors  = BitConverter.ToUInt32(_d, 44);
            uint firstDirSector = BitConverter.ToUInt32(_d, 48);
            _miniCutoff   = (int)BitConverter.ToUInt32(_d, 56);   // 보통 4096
            uint firstMiniFat = BitConverter.ToUInt32(_d, 60);
            uint numMiniFat   = BitConverter.ToUInt32(_d, 64);

            // FAT: 헤더 DIFAT(offset 76, 109개)로 FAT 섹터 위치를 얻어 조립.
            var fat = new List<uint>();
            for (int i = 0; i < 109 && i < numFatSectors; i++)
            {
                uint fatSec = BitConverter.ToUInt32(_d, 76 + i * 4);
                if (fatSec == ENDOFCHAIN || fatSec == 0xFFFFFFFF) break;
                ReadSectorUInts(fatSec, fat);
            }
            _fat = fat.ToArray();

            // 디렉터리 체인 → 엔트리 수집(레드블랙 트리 무시하고 선형 스캔).
            var dirBytes = ReadChain(firstDirSector);
            for (int off = 0; off + 128 <= dirBytes.Length; off += 128)
            {
                int type = dirBytes[off + 66];
                if (type == 0) continue;   // unallocated
                int nameLen = BitConverter.ToUInt16(dirBytes, off + 64);
                string name = nameLen > 2 ? System.Text.Encoding.Unicode.GetString(dirBytes, off, nameLen - 2) : "";
                uint startSec = BitConverter.ToUInt32(dirBytes, off + 116);
                long size     = (long)BitConverter.ToUInt64(dirBytes, off + 120);
                _dir.Add(new DirEntry(name, type, startSec, size));
            }

            // 루트(type 5) → mini stream 컨테이너 + mini-FAT.
            var root = _dir.First(e => e.Type == 5);
            _miniStream = ReadChain(root.StartSector, (int)root.Size);
            var mf = new List<uint>();
            if (numMiniFat > 0 && firstMiniFat != ENDOFCHAIN)
            {
                var mfBytes = ReadChain(firstMiniFat);
                for (int i = 0; i + 4 <= mfBytes.Length; i += 4) mf.Add(BitConverter.ToUInt32(mfBytes, i));
            }
            _miniFat = mf.ToArray();
        }

        public byte[]? ReadStreamByName(string name)
        {
            var e = _dir.FirstOrDefault(x => x.Type == 2 && x.Name == name);
            return e is null ? null : ReadEntry(e);
        }

        public List<(string Name, byte[] Data)> SectionStreams()
        {
            var res = new List<(string, byte[])>();
            foreach (var e in _dir.Where(x => x.Type == 2 && x.Name.StartsWith("Section"))
                                  .OrderBy(x => SecNum(x.Name)))
                res.Add((e.Name, ReadEntry(e)));
            return res;
        }

        static int SecNum(string n) => int.TryParse(n.AsSpan("Section".Length), out var v) ? v : 0;

        byte[] ReadEntry(DirEntry e)
        {
            if (e.Size < _miniCutoff)
            {
                // 작은 스트림 → mini stream 안에서 mini-FAT 체인으로.
                var outMs = new MemoryStream();
                uint sec = e.StartSector; long remain = e.Size;
                while (sec != ENDOFCHAIN && remain > 0)
                {
                    int off = (int)sec * _miniSecSize;
                    if (off < 0 || off + _miniSecSize > _miniStream.Length) break;
                    int len = (int)Math.Min(_miniSecSize, remain);
                    outMs.Write(_miniStream, off, len);
                    remain -= len;
                    sec = sec < (uint)_miniFat.Length ? _miniFat[sec] : ENDOFCHAIN;
                }
                return outMs.ToArray();
            }
            var full = ReadChain(e.StartSector);
            if (full.Length > e.Size) Array.Resize(ref full, (int)e.Size);
            return full;
        }

        byte[] ReadChain(uint start, int limit = int.MaxValue)
        {
            var outMs = new MemoryStream();
            uint sec = start; int written = 0;
            while (sec != ENDOFCHAIN && written < limit)
            {
                int off = _secSize + (int)sec * _secSize;   // 섹터 N → 파일오프셋 512 + N*secSize
                if (off < 0 || off + _secSize > _d.Length) break;
                int len = Math.Min(_secSize, limit - written);
                outMs.Write(_d, off, len);
                written += len;
                sec = sec < (uint)_fat.Length ? _fat[sec] : ENDOFCHAIN;
            }
            return outMs.ToArray();
        }

        void ReadSectorUInts(uint sec, List<uint> into)
        {
            int off = _secSize + (int)sec * _secSize;
            for (int i = 0; i + 4 <= _secSize && off + i + 4 <= _d.Length; i += 4)
                into.Add(BitConverter.ToUInt32(_d, off + i));
        }

        sealed record DirEntry(string Name, int Type, uint StartSector, long Size);
    }
}
