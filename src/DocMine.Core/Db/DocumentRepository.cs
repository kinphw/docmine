// MariaDB 연결 + DDL + 진단 — Python docmine/inserter.py 의 DB 부분 포팅.
//
// MySqlConnector 채택 이유 (vs MySql.Data):
//   - 진정한 async (MySql.Data 의 async 는 sync wrapper)
//   - MariaDB 호환 검증
//   - MIT, 활발 유지
//
// 적재 단계는 Phase 3/4 에서 추가. Phase 2 는 검색·DDL·진단만 사용.

using System.Text;
using MySqlConnector;
using DocMine.Core.Config;
using DocMine.Core.Pipeline;

namespace DocMine.Core.Db;

/// <summary>
/// 환경 간 이전(반출/반입) 단위 — body_text 까지 포함한 완전한 레코드.
/// id 는 환경마다 다르므로 전송 대상이 아니다(대상 환경이 자기 id 부여). 키는 (Directory,Filename).
/// </summary>
public sealed record DocRecord(
    string  Directory,
    string  Filename,
    string  Extension,
    long    FileSize,
    string? FileMtime,
    string? BodyText,
    string  ParseStatus,
    string? ErrorMsg,
    DateTime? ParsedAt);

public sealed class DocumentRepository
{
    private readonly AppConfig _cfg;

    public DocumentRepository(AppConfig cfg) => _cfg = cfg;

    /// <summary>config 의 기본 DB 사용. 진단 메시지를 OperationalException 에 첨부.</summary>
    public MySqlConnection OpenConnection(bool useDb = true)
    {
        var cs = _cfg.GetConnectionString(useDb);
        var conn = new MySqlConnection(cs);
        try
        {
            conn.Open();
        }
        catch (MySqlException ex)
        {
            conn.Dispose();
            var hint = DiagnoseDbError(ex.Message);
            // MySqlException 은 sealed + 외부 생성자 없음 → 자체 예외 타입으로 wrap.
            throw new DbConnectFailedException($"{ex.Message}\n{hint}", ex);
        }
        return conn;
    }

    /// <summary>OperationalError 등가 — diagnose 메시지를 첨부한 연결 실패.</summary>
    public sealed class DbConnectFailedException(string message, Exception inner)
        : Exception(message, inner);

    /// <summary>
    /// Python inserter._diagnose_db_error 의 등가물 — GUI 로그 패널에 멀티라인으로 표시.
    /// </summary>
    private string DiagnoseDbError(string msg)
    {
        var lines = new List<string>();

        lines.Add($"  settings: {_cfg.SettingsPath}");
        lines.Add($"  DB_USER='{_cfg.DbUser}', DB_HOST='{_cfg.DbHost}'");
        lines.Add("  → 값이 잘못됐다면 '⑤ 설정' 탭에서 수정 후 저장하세요.");

        if (msg.Contains("auth_gssapi_client"))
        {
            lines.Add("  → 'auth_gssapi_client' 는 Kerberos/AD 통합 인증 플러그인입니다.");
            lines.Add("    MySqlConnector 는 미지원 → 해당 DB 사용자가 mysql_native_password 로");
            lines.Add("    설정된 계정인지 확인하세요.");
        }
        else if (msg.Contains("Access denied"))
        {
            lines.Add("  → 사용자/비밀번호를 확인하세요.");
        }
        else if (msg.Contains("Unknown database"))
        {
            lines.Add($"  → DB '{_cfg.DbName}' 가 서버에 없습니다. 서버에서 CREATE DATABASE 필요.");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// DB/테이블/인덱스 생성 — Python inserter.create_db 와 동일 DDL.
    /// 이미 있으면 IF NOT EXISTS / try-catch 로 silent skip.
    /// 기존 Python 판이 만든 DB 가 있으면 그대로 호환 (스키마 변경 없음).
    /// </summary>
    public void EnsureDatabase()
    {
        // 1) DB 자체.
        using (var conn = OpenConnection(useDb: false))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{_cfg.DbName}` " +
                              "CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
            cmd.ExecuteNonQuery();
        }

        // 2) 테이블 + 인덱스.
        using (var conn = OpenConnection(useDb: true))
        {
            ExecuteSilent(conn, $@"
CREATE TABLE IF NOT EXISTS `{_cfg.DbTable}` (
    id           INT AUTO_INCREMENT PRIMARY KEY,
    directory    VARCHAR(1000)  NOT NULL,
    filename     VARCHAR(500)   NOT NULL,
    extension    VARCHAR(10)    NOT NULL,
    file_size    BIGINT         DEFAULT 0,
    file_mtime   VARCHAR(30),
    body_text    LONGTEXT,
    parse_status ENUM('success','error','skip','empty') DEFAULT 'success',
    error_msg    VARCHAR(1000),
    parsed_at    DATETIME       DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY   uq_file (directory(500), filename(255)),
    INDEX        idx_parse_status (parse_status),
    INDEX        idx_extension    (extension),
    INDEX        idx_filename     (filename(191))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");

            // ENUM 에 'empty' 가 없는 구버전 테이블 보강. 이미 있으면 메타데이터-only.
            ExecuteSilent(conn,
                $"ALTER TABLE `{_cfg.DbTable}` MODIFY COLUMN parse_status " +
                "ENUM('success','error','skip','empty') DEFAULT 'success'");

            // 기존 테이블에 인덱스 없으면 추가. 이미 있으면 ERROR 1061 — 무시.
            foreach (var ddl in new[]
            {
                $"CREATE INDEX idx_parse_status ON `{_cfg.DbTable}` (parse_status)",
                $"CREATE INDEX idx_extension    ON `{_cfg.DbTable}` (extension)",
                $"CREATE INDEX idx_filename     ON `{_cfg.DbTable}` (filename(191))",
            })
            {
                ExecuteSilent(conn, ddl);
            }
        }
    }

    private static void ExecuteSilent(MySqlConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (MySqlException) { /* 이미 존재 / Duplicate key 등 무시 */ }
    }

    /// <summary>레코드는 유지하되 body_text 만 NULL 처리 — 검색에서 제외.</summary>
    public int NullifyBodyText(IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0) return 0;
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var placeholders = string.Join(", ", ids.Select((_, i) => $"@id{i}"));
        cmd.CommandText = $"UPDATE `{_cfg.DbTable}` SET body_text = NULL WHERE id IN ({placeholders})";
        var i = 0;
        foreach (var id in ids) cmd.Parameters.AddWithValue($"@id{i++}", id);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// DB 전체의 (directory, filename) 만 가벼운 CSV 로 덤프 — 적재 현황 manifest.
    /// body_text 제외라 수십만 행도 수 MB. 환경2(법률검토)에서 뽑아 환경1 로 옮긴 뒤
    /// DbExportTab 에서 '이미 환경2 에 적재된 파일' 대조에 사용. 반환값 = 행 수.
    /// </summary>
    public int ExportManifestCsv(string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var w = new StreamWriter(outPath, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        w.WriteLine("directory,filename");

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT directory, filename FROM `{_cfg.DbTable}` ORDER BY directory, filename";
        using var rdr = cmd.ExecuteReader();
        var n = 0;
        while (rdr.Read())
        {
            w.Write(CsvIngestHelpers.CsvEscape(rdr.GetString(0))); w.Write(',');
            w.Write(CsvIngestHelpers.CsvEscape(rdr.GetString(1))); w.Write("\r\n");
            n++;
        }
        return n;
    }

    private static readonly HashSet<string> ValidStatus =
        new(StringComparer.OrdinalIgnoreCase) { "success", "error", "skip", "empty" };

    /// <summary>
    /// 선택된 id 들의 완전한 레코드(body_text 포함)를 조회 — 반출(전송 payload) 용.
    /// 목록 쿼리는 메타만 가볍게 두고, 본문은 반출 시 선택분만 여기서 끌어온다.
    /// </summary>
    public List<DocRecord> LoadRecordsByIds(IReadOnlyCollection<int> ids)
    {
        var result = new List<DocRecord>(ids.Count);
        if (ids.Count == 0) return result;

        var idList = ids.ToList();
        const int chunkSize = 500;
        using var conn = OpenConnection();
        for (var i = 0; i < idList.Count; i += chunkSize)
        {
            var chunk = idList.Skip(i).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select((_, j) => $"@id{j}"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT directory, filename, extension, file_size, file_mtime, " +
                $"body_text, parse_status, error_msg, parsed_at " +
                $"FROM `{_cfg.DbTable}` WHERE id IN ({placeholders})";
            for (var j = 0; j < chunk.Count; j++)
                cmd.Parameters.AddWithValue($"@id{j}", chunk[j]);

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                result.Add(new DocRecord(
                    Directory:   rdr.GetString(0),
                    Filename:    rdr.GetString(1),
                    Extension:   rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                    FileSize:    rdr.IsDBNull(3) ? 0  : rdr.GetInt64(3),
                    FileMtime:   rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    BodyText:    rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    ParseStatus: rdr.IsDBNull(6) ? "success" : rdr.GetString(6),
                    ErrorMsg:    rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    ParsedAt:    rdr.IsDBNull(8) ? null : rdr.GetDateTime(8)));
            }
        }
        return result;
    }

    /// <summary>
    /// 레코드(body_text 포함)를 (directory,filename) UNIQUE 키 기준으로 upsert — 반입.
    /// 재파싱 없이 다른 환경의 파싱 결과를 그대로 적재. 멱등(같은 CSV 재반입해도 안전).
    /// batchCommit 마다 커밋해 거대 트랜잭션을 피한다([[PressImporter.Upsert]] 와 같은 패턴) —
    /// 수만 건 반입 중 끊겨도 직전 커밋분은 남고, 멱등이라 재반입으로 이어서 완료된다.
    /// (단일 트랜잭션이면 마지막 한 건에서 실패해도 전량 롤백이라 처음부터 다시 해야 했다.)
    /// 반환: (신규 삽입 수, 기존 갱신 수).
    /// </summary>
    public (int Inserted, int Updated) UpsertRecords(
        IReadOnlyList<DocRecord> records,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default,
        int batchCommit = 2000)
    {
        int inserted = 0, updated = 0;
        if (records.Count == 0) return (0, 0);

        using var conn = OpenConnection();
        var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"
INSERT INTO `{_cfg.DbTable}`
    (directory, filename, extension, file_size, file_mtime, body_text, parse_status, error_msg, parsed_at)
VALUES (@d, @fn, @ext, @sz, @mt, @body, @status, @err, @pa)
ON DUPLICATE KEY UPDATE
    extension=VALUES(extension), file_size=VALUES(file_size), file_mtime=VALUES(file_mtime),
    body_text=VALUES(body_text), parse_status=VALUES(parse_status),
    error_msg=VALUES(error_msg), parsed_at=VALUES(parsed_at)";
        var pD   = cmd.Parameters.Add("@d",      MySqlDbType.VarChar);
        var pFn  = cmd.Parameters.Add("@fn",     MySqlDbType.VarChar);
        var pExt = cmd.Parameters.Add("@ext",    MySqlDbType.VarChar);
        var pSz  = cmd.Parameters.Add("@sz",     MySqlDbType.Int64);
        var pMt  = cmd.Parameters.Add("@mt",     MySqlDbType.VarChar);
        var pBody= cmd.Parameters.Add("@body",   MySqlDbType.LongText);
        var pSt  = cmd.Parameters.Add("@status", MySqlDbType.VarChar);
        var pErr = cmd.Parameters.Add("@err",    MySqlDbType.VarChar);
        var pPa  = cmd.Parameters.Add("@pa",     MySqlDbType.DateTime);

        try
        {
            for (var i = 0; i < records.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var r = records[i];
                // 키는 DB 적재본과 동일하게 NFC 정규화(스캔/추출 경로와 일관 — [[CsvIngestHelpers]]).
                pD.Value    = r.Directory.Normalize(NormalizationForm.FormC);
                pFn.Value   = r.Filename.Normalize(NormalizationForm.FormC);
                pExt.Value  = r.Extension ?? "";
                pSz.Value   = r.FileSize;
                pMt.Value   = (object?)r.FileMtime ?? DBNull.Value;
                pBody.Value = (object?)r.BodyText ?? DBNull.Value;
                pSt.Value   = ValidStatus.Contains(r.ParseStatus) ? r.ParseStatus.ToLowerInvariant() : "success";
                pErr.Value  = (object?)r.ErrorMsg ?? DBNull.Value;
                pPa.Value   = (object?)r.ParsedAt ?? DBNull.Value;

                var affected = cmd.ExecuteNonQuery();
                if (affected == 1) inserted++; else updated++;   // 1=삽입, 2=갱신, 0=동일(기존으로 셈)

                // batchCommit 마다 끊어 커밋 — 여기까지는 중단/실패해도 남는다.
                if ((i + 1) % batchCommit == 0)
                {
                    tx.Commit(); tx.Dispose();
                    onProgress?.Invoke(i + 1, records.Count);
                    tx = conn.BeginTransaction();
                    cmd.Transaction = tx;
                }
                else if ((i & 0x3F) == 0) onProgress?.Invoke(i + 1, records.Count);
            }
            tx.Commit();
        }
        finally { tx.Dispose(); }

        onProgress?.Invoke(records.Count, records.Count);
        return (inserted, updated);
    }

    /// <summary>선택 id 들의 본문(body_text)만 id→텍스트로 조회 — '본문 클립보드 복사' 용.</summary>
    public Dictionary<int, string?> LoadBodyByIds(IReadOnlyCollection<int> ids)
    {
        var result = new Dictionary<int, string?>(ids.Count);
        if (ids.Count == 0) return result;

        var idList = ids.ToList();
        const int chunkSize = 500;
        using var conn = OpenConnection();
        for (var i = 0; i < idList.Count; i += chunkSize)
        {
            var chunk = idList.Skip(i).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select((_, j) => $"@id{j}"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id, body_text FROM `{_cfg.DbTable}` WHERE id IN ({placeholders})";
            for (var j = 0; j < chunk.Count; j++) cmd.Parameters.AddWithValue($"@id{j}", chunk[j]);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                result[rdr.GetInt32(0)] = rdr.IsDBNull(1) ? null : rdr.GetString(1);
        }
        return result;
    }

    /// <summary>현재 DB 전체의 (directory, filename) 원본 쌍 — 반입 대조(현재환경) 용.</summary>
    public List<(string Dir, string Fn)> LoadAllKeys()
    {
        var list = new List<(string, string)>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT directory, filename FROM `{_cfg.DbTable}`";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) list.Add((rdr.GetString(0), rdr.GetString(1)));
        return list;
    }

    /// <summary>레코드 자체를 DB 에서 완전히 삭제.</summary>
    public int DeleteRows(IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0) return 0;
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var placeholders = string.Join(", ", ids.Select((_, i) => $"@id{i}"));
        cmd.CommandText = $"DELETE FROM `{_cfg.DbTable}` WHERE id IN ({placeholders})";
        var i = 0;
        foreach (var id in ids) cmd.Parameters.AddWithValue($"@id{i++}", id);
        return cmd.ExecuteNonQuery();
    }
}
