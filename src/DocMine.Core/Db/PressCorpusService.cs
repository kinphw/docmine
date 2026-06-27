// PressCorpusService — 외부 stn_press_db.press_document 읽기전용 검색.
//
// 외부 프로젝트(stn-crawler)가 적재한 4대 기관(금융위·금감원·기재부·한국은행)
// 보도자료 코퍼스를 DocMine 검색에 합치기 위한 서비스. 결합점은 DB 스키마 하나뿐
// (docs/press-corpus-integration.md §4 결합 계약). DocMine 은 SELECT 만 한다.
//
// 설계 메모:
//   - 메인 documents 검색([[SearchService]])과 '같은 의미'의 Count/Search 를 제공하되
//     press 컬럼(content/file_name/folder/published_date)에 매핑한다. 결과 병합은
//     호출자(SearchTab)가 메모리에서 수행 — SearchTab 이 이미 '전체 로드 + 클라이언트
//     정렬' 구조라 두 결과를 이어붙이기만 하면 기존 정렬·가상화가 그대로 동작한다.
//   - "있는 경우에만": IsAvailable() 1회 프로브로 환경1(press 없음)/환경2(있음)을
//     자동 분기. 실패(테이블 없음·권한 없음·DB 없음)는 조용히 false → 검색에서 스킵.
//   - 본문이 이미 DB(content)에 추출돼 있어 파싱·COM·워커·DRM 회피 로직이 전혀 불필요.
//     즉 DocMine 의 DRM/워커 불변식과 무관(파일 내용 read 가 아니라 DB 텍스트 read).
//   - 스니펫·키워드 처리는 SearchService 의 static 헬퍼를 재사용한다.

using MySqlConnector;
using DocMine.Core.Config;

namespace DocMine.Core.Db;

/// <summary>press_document 한 행(검색 결과). body 는 LEFT(content,5000) 만 — 스니펫용.</summary>
public sealed record PressSearchRow(
    int       Id,
    string    Source,        // fsc / fss / moef / bok
    string    Folder,        // '날짜_제목' 다운로드 폴더명
    string    FileName,
    string?   ContentChunk,  // NULL/빈 = 추출 실패/이미지성(제외 취급)
    DateTime? PublishedDate);

/// <summary>반출 대조용 메타데이터(본문 제외) — 반출이력과 비교해 신규/변경 판정.</summary>
public sealed record PressExportMeta(
    int       Id,
    string    Source,
    string    SourceSeq,
    string    Folder,
    string    FileName,
    string    FileExt,
    int       CharCount,
    DateTime? PublishedDate,
    string    ContentHash);   // 변경 감지 키 (sha256(content))

/// <summary>반출 전송본 한 행(본문 content 포함) — 상대 환경 press_document 적재용.</summary>
public sealed record PressFullRecord(
    string    Source,
    string    SourceSeq,
    string    Folder,
    DateTime? PublishedDate,
    string    PostTitle,
    string    FileName,
    string    FileExt,
    string    FileUrl,
    string?   Content,
    int       CharCount,
    string    ContentHash);

public sealed class PressCorpusService
{
    private readonly AppConfig _cfg;
    private bool? _available;   // 프로브 결과 캐시(인스턴스 수명 동안). 설정 변경 시 새 인스턴스로 갱신.

    public PressCorpusService(AppConfig cfg) => _cfg = cfg;

    private MySqlConnection OpenConnection()
    {
        var conn = new MySqlConnection(_cfg.GetPressConnectionString());
        conn.Open();
        return conn;
    }

    /// <summary>
    /// press 테이블 접근 가능 여부 — 1회 프로브 후 캐시. 실패는 모두 조용히 false.
    /// (환경1처럼 DB/테이블/권한이 없으면 여기서 걸러져 검색에 포함되지 않는다.)
    /// </summary>
    public bool IsAvailable()
    {
        if (!_cfg.PressEnabled) return false;
        if (_available is not null) return _available.Value;
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM press_document LIMIT 1";
            cmd.ExecuteScalar();
            _available = true;
        }
        catch
        {
            _available = false;
        }
        return _available.Value;
    }

    // ─ WHERE 빌더 — press 컬럼 매핑. SearchService.ComposeWhere 와 같은 의미 ──────
    // press 는 ID 범위·적재일 범위 개념이 없으므로(별도 id 공간) 키워드/대상/제외만 받는다.
    private static (string WhereSql, List<object> Params) ComposeWhere(
        string keyword, SearchTarget target, SearchMode mode, bool includeExcluded)
    {
        var keywords = SearchService.PrepareKeywords(keyword, mode);
        var conds = new List<string>();
        var prms  = new List<object>();

        // content 가 비면 추출 실패/이미지성 → '제외' 항목으로 간주(로컬 body_text 비움과 동형).
        if (!includeExcluded)
            conds.Add("content IS NOT NULL AND content <> ''");

        if (keywords.Count > 0)
        {
            var kwParts = new List<string>();
            foreach (var kw in keywords)
            {
                var like = $"%{kw}%";
                if (target == SearchTarget.Title)
                {
                    // press 의 '제목' = 게시물 제목(post_title) + 파일명(file_name) 둘 다.
                    kwParts.Add("(post_title LIKE ? OR file_name LIKE ?)");
                    prms.Add(like); prms.Add(like);
                }
                else if (target == SearchTarget.Body)
                {
                    kwParts.Add("content LIKE ?");
                    prms.Add(like);
                }
                else // Both
                {
                    kwParts.Add("(post_title LIKE ? OR file_name LIKE ? OR content LIKE ?)");
                    prms.Add(like); prms.Add(like); prms.Add(like);
                }
            }
            var joiner = mode == SearchMode.Or ? " OR " : " AND ";
            conds.Add("(" + string.Join(joiner, kwParts) + ")");
        }

        var whereSql = conds.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conds);
        return (whereSql, prms);
    }

    /// <summary>조건에 맞는 press 문서 수. 미가용이면 0.</summary>
    public int Count(string keyword, SearchTarget target, SearchMode mode, bool includeExcluded)
    {
        if (!IsAvailable()) return 0;
        var (whereSql, prms) = ComposeWhere(keyword, target, mode, includeExcluded);
        var sql = $"SELECT COUNT(*) FROM press_document{whereSql}";

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in prms) cmd.Parameters.Add(new MySqlParameter { Value = p });
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    /// <summary>
    /// 조건에 맞는 press 문서를 limit 만큼 조회. 미가용이면 빈 목록.
    /// content 는 LEFT 5000자만(로컬과 동일) — 클라이언트 스니펫 추출용.
    /// 최신 보도가 먼저 보이도록 published_date DESC 정렬.
    /// </summary>
    public IReadOnlyList<PressSearchRow> Search(
        string keyword, SearchTarget target, SearchMode mode,
        int limit, bool includeExcluded)
    {
        if (!IsAvailable() || limit <= 0) return Array.Empty<PressSearchRow>();
        var (whereSql, prms) = ComposeWhere(keyword, target, mode, includeExcluded);
        // 키워드 위치 주변만 잘라옴 — 로컬 검색([[SearchService]])과 동일한 키워드 중심 스니펫.
        var centerKw = SearchService.PrepareKeywords(keyword, mode) is { Count: > 0 } ks ? ks[0] : "";
        var sql = $@"
            SELECT id, source, folder, file_name,
                   SUBSTRING(content, GREATEST(1, LOCATE(?, content) - {SearchService.SnippetLeadChars}), {SearchService.SnippetWindowChars}),
                   published_date
            FROM press_document
            {whereSql}
            ORDER BY published_date DESC, id DESC
            LIMIT ?";

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        // 파라미터 순서 = SQL 의 ? 순서: (1) SELECT 의 LOCATE, (2) WHERE, (3) LIMIT.
        cmd.Parameters.Add(new MySqlParameter { Value = centerKw });
        foreach (var p in prms) cmd.Parameters.Add(new MySqlParameter { Value = p });
        cmd.Parameters.Add(new MySqlParameter { Value = limit });

        var rows = new List<PressSearchRow>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            rows.Add(new PressSearchRow(
                Id:            rdr.GetInt32(0),
                Source:        rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                Folder:        rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                FileName:      rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                ContentChunk:  rdr.IsDBNull(4) ? null : rdr.GetString(4),
                PublishedDate: rdr.IsDBNull(5) ? null : rdr.GetDateTime(5)));
        }
        return rows;
    }

    // ─ 반출(증분) 지원 ───────────────────────────────────────────────

    /// <summary>
    /// 반출 대조용 전체 메타데이터(본문 제외) — 본문 있는 행만. 반출이력과 비교해
    /// 신규/변경분을 가린다. 본문을 안 실어 6.8만 행도 가볍다.
    /// </summary>
    public IReadOnlyList<PressExportMeta> LoadExportMeta()
    {
        if (!IsAvailable()) return Array.Empty<PressExportMeta>();
        const string sql = @"
            SELECT id, source, source_seq, folder, file_name, file_ext,
                   char_count, published_date, content_hash
            FROM press_document
            WHERE content IS NOT NULL AND content <> ''
            ORDER BY source, published_date, id";

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var rows = new List<PressExportMeta>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            rows.Add(new PressExportMeta(
                Id:            rdr.GetInt32(0),
                Source:        rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                SourceSeq:     rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                Folder:        rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                FileName:      rdr.IsDBNull(4) ? "" : rdr.GetString(4),
                FileExt:       rdr.IsDBNull(5) ? "" : rdr.GetString(5),
                CharCount:     rdr.IsDBNull(6) ? 0  : rdr.GetInt32(6),
                PublishedDate: rdr.IsDBNull(7) ? null : rdr.GetDateTime(7),
                ContentHash:   rdr.IsDBNull(8) ? "" : rdr.GetString(8)));
        }
        return rows;
    }

    /// <summary>
    /// 선택된 id 들의 전송본(본문 content 포함) 조회 — 반출 CSV 기록용.
    /// 호출자(PressTransferCsv)가 청크 단위로 끊어 호출해 대용량 본문 메모리 피크를 피한다.
    /// </summary>
    public List<PressFullRecord> LoadExportRecords(IReadOnlyCollection<int> ids)
    {
        var result = new List<PressFullRecord>(ids.Count);
        if (!IsAvailable() || ids.Count == 0) return result;

        var placeholders = string.Join(", ", ids.Select((_, i) => $"@id{i}"));
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT source, source_seq, folder, published_date, post_title,
                   file_name, file_ext, file_url, content, char_count, content_hash
            FROM press_document WHERE id IN ({placeholders})";
        var i = 0;
        foreach (var id in ids) cmd.Parameters.AddWithValue($"@id{i++}", id);

        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            result.Add(new PressFullRecord(
                Source:        rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                SourceSeq:     rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                Folder:        rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                PublishedDate: rdr.IsDBNull(3) ? null : rdr.GetDateTime(3),
                PostTitle:     rdr.IsDBNull(4) ? "" : rdr.GetString(4),
                FileName:      rdr.IsDBNull(5) ? "" : rdr.GetString(5),
                FileExt:       rdr.IsDBNull(6) ? "" : rdr.GetString(6),
                FileUrl:       rdr.IsDBNull(7) ? "" : rdr.GetString(7),
                Content:       rdr.IsDBNull(8) ? null : rdr.GetString(8),
                CharCount:     rdr.IsDBNull(9) ? 0  : rdr.GetInt32(9),
                ContentHash:   rdr.IsDBNull(10) ? "" : rdr.GetString(10)));
        }
        return result;
    }

    /// <summary>기관 코드 → 한글 라벨. UI 출처 표시·원본 경로 구성에 공용.</summary>
    public static string SourceLabel(string? source) => source switch
    {
        "fsc"  => "금융위",
        "fss"  => "금감원",
        "moef" => "기재부",
        "bok"  => "한국은행",
        _      => source ?? "",
    };
}
