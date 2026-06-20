// CSV 로딩 + DB 기존 키 조회 — HWP/PDF Runner 공유 헬퍼.
// 원래 PdfInsertRunner 안에 있던 private 메서드를 분리.

using DocMine.Core.Config;
using DocMine.Core.Db;

namespace DocMine.Core.Pipeline;

public sealed record CsvRow(
    string Directory,
    string Filename,
    string Extension,
    long   SizeBytes,
    string Modified);

public static class CsvIngestHelpers
{
    /// <summary>utf-8-sig BOM 처리 + 컬럼명 매핑.</summary>
    public static List<CsvRow> LoadCsv(string csvPath)
    {
        var rows = new List<CsvRow>();
        using var reader = new StreamReader(csvPath, new System.Text.UTF8Encoding(true));
        var header = reader.ReadLine();
        if (header is null) return rows;

        var cols = header.Split(',')
            .Select((c, i) => (Name: c.Trim().Trim('﻿'), Idx: i))
            .ToDictionary(p => p.Name, p => p.Idx, StringComparer.OrdinalIgnoreCase);
        int Col(string name) => cols.TryGetValue(name, out var i) ? i : -1;
        var iDir = Col("directory"); var iFn = Col("filename");
        var iExt = Col("extension"); var iSz = Col("size_bytes"); var iMt = Col("modified");

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = ParseCsvLine(line);
            if (parts.Count <= Math.Max(iDir, iFn)) continue;
            rows.Add(new CsvRow(
                Directory: parts[iDir],
                Filename:  parts[iFn],
                Extension: iExt >= 0 && iExt < parts.Count ? parts[iExt] : "",
                SizeBytes: iSz >= 0 && iSz < parts.Count && long.TryParse(parts[iSz], out var sz) ? sz : 0,
                Modified:  iMt >= 0 && iMt < parts.Count ? parts[iMt] : ""));
        }
        return rows;
    }

    public static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuote)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuote = false;
                }
                else sb.Append(ch);
            }
            else
            {
                if (ch == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else if (ch == '"' && sb.Length == 0) inQuote = true;
                else sb.Append(ch);
            }
        }
        result.Add(sb.ToString());
        return result;
    }

    /// <summary>
    /// (directory, filename) skip-키 정규화.
    ///
    /// 스캔 CSV 의 문자열과 DB 적재본의 문자열이 Unicode 정규화형(NFC/NFD)·
    /// 대소문자·앞뒤 공백에서 어긋나면, 같은 파일인데도 ordinal 비교로는 다른 키가
    /// 되어 skip 에 실패한다(특히 OneDrive 한글 경로). 그 한 건만 재파싱·재전송되어
    /// 거대 본문 OOM 으로 메인 GUI 가 silent 종료되던 운영 환경 크래시의 시발점.
    ///   - NFC 통일 (Windows 파일 시스템은 보통 NFC)
    ///   - 앞뒤 공백 제거
    ///   - 대소문자 무시 (Windows 경로는 case-insensitive)
    /// </summary>
    public static (string, string) NormKey(string directory, string filename)
        => (NormName(directory), NormName(filename));

    /// <summary>단일 문자열(파일명 등) 정규화 — NormKey 와 동일 규칙
    /// (NFC + trim + lowercase). 파일명만 비교(폴더 무관)할 때 사용.</summary>
    public static string NormName(string s)
        => (s ?? string.Empty)
            .Normalize(System.Text.NormalizationForm.FormC)
            .Trim()
            .ToLowerInvariant();

    /// <summary>(directory, filename) chunked lookup — 이미 DB 에 있는 행은 skip.
    /// 반환 집합은 <see cref="NormKey"/> 로 정규화된 키. 호출부도 NormKey 로 비교할 것.</summary>
    public static HashSet<(string, string)> LoadExistingKeys(
        AppConfig cfg, DocumentRepository repo, List<CsvRow> rows)
    {
        var candidates = rows
            .Where(r => !string.IsNullOrEmpty(r.Directory) && !string.IsNullOrEmpty(r.Filename))
            .Select(r => (r.Directory, r.Filename))
            .Distinct()
            .ToList();
        var existing = new HashSet<(string, string)>();
        if (candidates.Count == 0) return existing;

        const int chunkSize = 500;
        using var conn = repo.OpenConnection();
        for (var i = 0; i < candidates.Count; i += chunkSize)
        {
            var chunk = candidates.Skip(i).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select((_, j) => $"(@d{j}, @f{j})"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT directory, filename FROM `{cfg.DbTable}` " +
                $"WHERE (directory, filename) IN ({placeholders})";
            for (var j = 0; j < chunk.Count; j++)
            {
                // NFC 로 정규화해 보냄 — NFC 로 적재된 DB 행과 매칭 (NFD 스캔 결과 보정).
                cmd.Parameters.AddWithValue($"@d{j}",
                    chunk[j].Directory.Normalize(System.Text.NormalizationForm.FormC));
                cmd.Parameters.AddWithValue($"@f{j}",
                    chunk[j].Filename.Normalize(System.Text.NormalizationForm.FormC));
            }
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                existing.Add(NormKey(rdr.GetString(0), rdr.GetString(1)));
        }
        return existing;
    }

    /// <summary>
    /// 적재 현황 manifest CSV(directory,filename 2컬럼) → NormKey 집합.
    /// 환경2 DB 에서 ExportManifestCsv 로 뽑은 파일을 환경1 에서 대조할 때 사용.
    /// 컬럼명으로 매핑하므로 추가 컬럼이 있어도 directory/filename 만 읽는다.
    /// </summary>
    public static HashSet<(string, string)> LoadManifestKeys(string csvPath)
    {
        var set = new HashSet<(string, string)>();
        using var reader = new StreamReader(csvPath, new System.Text.UTF8Encoding(true));
        var header = reader.ReadLine();
        if (header is null) return set;

        var cols = header.Split(',')
            .Select((c, i) => (Name: c.Trim().Trim('﻿'), Idx: i))
            .ToDictionary(p => p.Name, p => p.Idx, StringComparer.OrdinalIgnoreCase);
        var iDir = cols.TryGetValue("directory", out var a) ? a : 0;
        var iFn  = cols.TryGetValue("filename",  out var b) ? b : 1;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = ParseCsvLine(line);
            if (parts.Count <= Math.Max(iDir, iFn)) continue;
            set.Add(NormKey(parts[iDir], parts[iFn]));
        }
        return set;
    }

    /// <summary>
    /// manifest CSV(directory,filename) → 원본 (정규화 안 한) 쌍 목록.
    /// 합집합 대조에서 '대조에만 있는' 유령 행을 원본 표기로 보여줄 때 사용
    /// (LoadManifestKeys 는 매칭용 정규화 집합이라 대소문자가 뭉개진다).
    /// </summary>
    public static List<(string Dir, string Fn)> LoadManifestRows(string csvPath)
    {
        var rows = new List<(string, string)>();
        using var reader = new StreamReader(csvPath, new System.Text.UTF8Encoding(true));
        var header = reader.ReadLine();
        if (header is null) return rows;

        var cols = header.Split(',')
            .Select((c, i) => (Name: c.Trim().Trim('﻿'), Idx: i))
            .ToDictionary(p => p.Name, p => p.Idx, StringComparer.OrdinalIgnoreCase);
        var iDir = cols.TryGetValue("directory", out var a) ? a : 0;
        var iFn  = cols.TryGetValue("filename",  out var b) ? b : 1;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = ParseCsvLine(line);
            if (parts.Count <= Math.Max(iDir, iFn)) continue;
            if (string.IsNullOrEmpty(parts[iDir]) && string.IsNullOrEmpty(parts[iFn])) continue;
            rows.Add((parts[iDir], parts[iFn]));
        }
        return rows;
    }

    /// <summary>CSV 필드 escape — 콤마/따옴표/개행 포함 시 따옴표로 감싸고 내부 따옴표 중복.</summary>
    public static string CsvEscape(string s)
        => s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
            ? s
            : "\"" + s.Replace("\"", "\"\"") + "\"";
}
