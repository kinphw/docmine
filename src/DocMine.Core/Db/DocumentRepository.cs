// MariaDB 연결 + DDL + 진단 — Python docmine/inserter.py 의 DB 부분 포팅.
//
// MySqlConnector 채택 이유 (vs MySql.Data):
//   - 진정한 async (MySql.Data 의 async 는 sync wrapper)
//   - MariaDB 호환 검증
//   - MIT, 활발 유지
//
// 적재 단계는 Phase 3/4 에서 추가. Phase 2 는 검색·DDL·진단만 사용.

using MySqlConnector;
using DocMine.Core.Config;

namespace DocMine.Core.Db;

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
