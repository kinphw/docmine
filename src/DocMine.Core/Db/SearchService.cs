// 검색 쿼리·스니펫·키워드 처리 — Python docmine/search_gui.py 의 모듈 함수 등가.
//
// UI 무의존 — search_gui.App 분리 시 _compose_where / _extract_snippet / search /
// count_results 를 그대로 끌어왔다. 단위 테스트 대상이 이 클래스에 모임.

using System.Globalization;
using System.Text;
using MySqlConnector;
using DocMine.Core.Config;

namespace DocMine.Core.Db;

public enum SearchTarget { Both, Title, Body }
public enum SearchMode   { And, Or, Phrase }

public sealed record SearchRow(
    int    Id,
    string Directory,
    string Filename,
    string? BodyChunk,       // NULL/빈 = 제외 항목
    DateTime? ParsedAt);     // 최종 적재(파싱) 시각 — documents.parsed_at

// DB 추출(⑤) 전용 — body_text 제외한 메타데이터 전체 컬럼.
public sealed record ExportRow(
    int       Id,
    string    Directory,
    string    Filename,
    string    Extension,
    long      FileSize,
    string?   FileMtime,
    string    ParseStatus,
    DateTime? ParsedAt);

public sealed class SearchService
{
    public const int PageSize = 200;

    // 키워드 양옆에 잡을 문자 수 — 비대칭 (search_gui.py 의 SNIPPET_LEFT/RIGHT).
    // 미리보기 셀이 좁아 키워드를 셀 좌측 가까이 끌어오기 위해 left << right.
    public const int SnippetLeft  = 20;
    public const int SnippetRight = 220;

    private readonly AppConfig _cfg;

    public SearchService(AppConfig cfg) => _cfg = cfg;

    // ─ 키워드 파싱 ────────────────────────────────────────────────────
    public static IReadOnlyList<string> PrepareKeywords(string keyword, SearchMode mode)
    {
        if (mode == SearchMode.Phrase)
            return string.IsNullOrEmpty(keyword) ? Array.Empty<string>() : new[] { keyword };
        if (string.IsNullOrWhiteSpace(keyword)) return Array.Empty<string>();
        return keyword.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    }

    // ─ 스니펫 추출 — Python _extract_snippet 등가 ─────────────────────
    public static string ExtractSnippet(
        string? body,
        IReadOnlyList<string> keywords,
        int left = SnippetLeft,
        int right = SnippetRight)
    {
        if (string.IsNullOrEmpty(body)) return "";
        body = body.Replace("\r", "").Replace('\n', ' ');
        var fallbackLen = left + right + 30;

        if (keywords.Count == 0)
            return body.Length <= fallbackLen ? body.Trim() : body[..fallbackLen].Trim();

        // 모든 키워드의 첫 매치 중 가장 빠른 위치 채택 (대소문자 무시).
        var bodyLower = body.ToLowerInvariant();
        int bestPos = -1, bestKwLen = 0;
        foreach (var kw in keywords)
        {
            if (string.IsNullOrEmpty(kw)) continue;
            var pos = bodyLower.IndexOf(kw.ToLowerInvariant(), StringComparison.Ordinal);
            if (pos < 0) continue;
            if (bestPos < 0 || pos < bestPos) { bestPos = pos; bestKwLen = kw.Length; }
        }

        if (bestPos < 0)
            return body.Length <= fallbackLen ? body.Trim() : body[..fallbackLen].Trim();

        var startIdx = Math.Max(0, bestPos - left);
        var endIdx   = Math.Min(body.Length, bestPos + bestKwLen + right);
        var snippet = body[startIdx..endIdx].Trim();
        if (startIdx > 0)         snippet = "…" + snippet;
        if (endIdx < body.Length) snippet = snippet + "…";
        return snippet;
    }

    // ─ WHERE 빌더 — Python _compose_where 등가 ────────────────────────
    // (sql, params) 반환. 호출자가 prefix(SELECT/COUNT) + suffix(ORDER BY/LIMIT) 붙임.
    public (string WhereSql, IReadOnlyList<object> Params) ComposeWhere(
        string keyword, SearchTarget target, SearchMode mode,
        bool includeExcluded, int? idMin = null, int? idMax = null,
        DateTime? parsedFrom = null, DateTime? parsedTo = null)
    {
        var keywords = PrepareKeywords(keyword, mode);
        var conds = new List<string>();
        var prms  = new List<object>();

        if (!includeExcluded)
            conds.Add("body_text IS NOT NULL AND body_text != ''");

        if (keywords.Count > 0)
        {
            var kwParts = new List<string>();
            foreach (var kw in keywords)
            {
                var like = $"%{kw}%";
                if (target == SearchTarget.Title)
                {
                    kwParts.Add("filename LIKE ?");
                    prms.Add(like);
                }
                else if (target == SearchTarget.Body)
                {
                    kwParts.Add("body_text LIKE ?");
                    prms.Add(like);
                }
                else // Both
                {
                    kwParts.Add("(filename LIKE ? OR body_text LIKE ?)");
                    prms.Add(like); prms.Add(like);
                }
            }
            var joiner = mode == SearchMode.Or ? " OR " : " AND ";
            conds.Add("(" + string.Join(joiner, kwParts) + ")");
        }

        if (idMin is not null) { conds.Add("id >= ?"); prms.Add(idMin.Value); }
        if (idMax is not null) { conds.Add("id <= ?"); prms.Add(idMax.Value); }

        // 적재일 범위 — 호출자가 To 를 '다음날 00:00' 으로 넘기면 그날 전체 포함 (반열림 구간).
        if (parsedFrom is not null) { conds.Add("parsed_at >= ?"); prms.Add(parsedFrom.Value); }
        if (parsedTo   is not null) { conds.Add("parsed_at <  ?"); prms.Add(parsedTo.Value); }

        var whereSql = conds.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conds);
        return (whereSql, prms);
    }

    // ─ 실행 ──────────────────────────────────────────────────────────
    public IReadOnlyList<SearchRow> Search(
        string keyword, SearchTarget target, SearchMode mode,
        int limit = PageSize, int offset = 0,
        bool includeExcluded = false, int? idMin = null, int? idMax = null,
        DateTime? parsedFrom = null, DateTime? parsedTo = null)
    {
        var (whereSql, prms) = ComposeWhere(keyword, target, mode, includeExcluded, idMin, idMax, parsedFrom, parsedTo);
        // body_text 5000자까지만 가져와서 클라이언트 스니펫 추출. 200건 × 5KB ≈ 1MB.
        // 5000 자 내 키워드 없으면 ExtractSnippet 이 앞부분 폴백.
        var sql = $@"
            SELECT id, directory, filename, LEFT(body_text, 5000), parsed_at
            FROM `{_cfg.DbTable}`
            {whereSql}
            ORDER BY id
            LIMIT ? OFFSET ?";

        using var conn = new DocumentRepository(_cfg).OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in prms)             cmd.Parameters.Add(new MySqlParameter { Value = p });
        cmd.Parameters.Add(new MySqlParameter { Value = limit });
        cmd.Parameters.Add(new MySqlParameter { Value = offset });

        var rows = new List<SearchRow>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            rows.Add(new SearchRow(
                Id:        rdr.GetInt32(0),
                Directory: rdr.GetString(1),
                Filename:  rdr.GetString(2),
                BodyChunk: rdr.IsDBNull(3) ? null : rdr.GetString(3),
                ParsedAt:  rdr.IsDBNull(4) ? null : rdr.GetDateTime(4)));
        }
        return rows;
    }

    public int CountResults(
        string keyword, SearchTarget target, SearchMode mode,
        bool includeExcluded = false, int? idMin = null, int? idMax = null,
        DateTime? parsedFrom = null, DateTime? parsedTo = null)
    {
        var (whereSql, prms) = ComposeWhere(keyword, target, mode, includeExcluded, idMin, idMax, parsedFrom, parsedTo);
        var sql = $"SELECT COUNT(*) FROM `{_cfg.DbTable}`{whereSql}";

        using var conn = new DocumentRepository(_cfg).OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in prms) cmd.Parameters.Add(new MySqlParameter { Value = p });
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    // ─ DB 추출(⑤) 전용 — 메타데이터 전체 컬럼, body_text 제외 ─────────
    /// <summary>검색 조건에 맞는 행을 메타데이터 컬럼으로 추출 (CSV 출력용).
    /// body_text 는 가져오지 않음. limit 은 메모리 폭주 방지 안전 상한.</summary>
    public IReadOnlyList<ExportRow> SearchForExport(
        string keyword, SearchTarget target, SearchMode mode,
        bool includeExcluded = false, int? idMin = null, int? idMax = null,
        DateTime? parsedFrom = null, DateTime? parsedTo = null,
        int limit = 200_000)
    {
        var (whereSql, prms) = ComposeWhere(keyword, target, mode, includeExcluded, idMin, idMax, parsedFrom, parsedTo);
        var sql = $@"
            SELECT id, directory, filename, extension, file_size, file_mtime, parse_status, parsed_at
            FROM `{_cfg.DbTable}`
            {whereSql}
            ORDER BY id
            LIMIT ?";

        using var conn = new DocumentRepository(_cfg).OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in prms) cmd.Parameters.Add(new MySqlParameter { Value = p });
        cmd.Parameters.Add(new MySqlParameter { Value = limit });

        var rows = new List<ExportRow>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            rows.Add(new ExportRow(
                Id:          rdr.GetInt32(0),
                Directory:   rdr.GetString(1),
                Filename:    rdr.GetString(2),
                Extension:   rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                FileSize:    rdr.IsDBNull(4) ? 0  : rdr.GetInt64(4),
                FileMtime:   rdr.IsDBNull(5) ? null : rdr.GetString(5),
                ParseStatus: rdr.IsDBNull(6) ? "" : rdr.GetString(6),
                ParsedAt:    rdr.IsDBNull(7) ? null : rdr.GetDateTime(7)));
        }
        return rows;
    }

    /// <summary>ExportRow 목록을 CSV 로 저장 — DriveScanner.WriteCsv 와 동일한
    /// utf-8-sig BOM 포맷. 컬럼: id,directory,filename,extension,size_bytes,
    /// modified,parse_status,parsed_at.</summary>
    public static void WriteExportCsv(IEnumerable<ExportRow> rows, string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var w = new StreamWriter(outPath, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        w.WriteLine("id,directory,filename,extension,size_bytes,modified,parse_status,parsed_at");
        foreach (var r in rows)
        {
            w.Write(r.Id.ToString(CultureInfo.InvariantCulture));     w.Write(',');
            w.Write(CsvEscape(r.Directory));                          w.Write(',');
            w.Write(CsvEscape(r.Filename));                           w.Write(',');
            w.Write(CsvEscape(r.Extension));                          w.Write(',');
            w.Write(r.FileSize.ToString(CultureInfo.InvariantCulture)); w.Write(',');
            w.Write(CsvEscape(r.FileMtime ?? ""));                    w.Write(',');
            w.Write(CsvEscape(r.ParseStatus));                        w.Write(',');
            w.Write(CsvEscape(r.ParsedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? ""));
            w.Write("\r\n");
        }
    }

    private static string CsvEscape(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
