// PressImporter — 보도자료(press) 반입(쓰기) 경로.
//
// 환경2에서 반출한 press CSV(PressTransferCsv)를 환경1의 press_document 에 적재한다.
// 검색·반출은 읽기전용([[PressCorpusService]], pdbuser)이지만, 반입은 쓰기가 필요하므로
// press 설정 계정이 환경1에선 쓰기 권한이어야 한다(같은 설정값을 환경별로 다르게).
//
//   - EnsureSchema(): DB·press_document 테이블이 없으면 생성(첫 반입). 스키마는 stn-crawler
//     sql/001_schema.sql 과 동일(결합 계약). 환경2에서 적재하는 것과 무관한 별개 작업.
//   - LoadExistingKeys(): 이미 반입된 (source, source_seq, file_name) — CSV 대조용.
//   - Upsert(): UNIQUE(source, source_seq, file_name) 기준 upsert. 멱등(재반입 안전).
//     content 가 커서 스트리밍 입력 + 배치 커밋으로 거대 트랜잭션/메모리 피크를 피한다.

using MySqlConnector;
using DocMine.Core.Config;

namespace DocMine.Core.Db;

/// <summary>반입 목록 표시용 경량 메타(본문 제외) — CSV 스트리밍에서 추출.</summary>
public sealed record PressImportMeta(
    string    Source,
    string    SourceSeq,
    string    Folder,
    DateTime? PublishedDate,
    string    FileName,
    string    FileExt,
    int       CharCount,
    bool      HasBody);

public sealed class PressImporter
{
    private readonly AppConfig _cfg;

    public PressImporter(AppConfig cfg) => _cfg = cfg;

    private MySqlConnection Open(bool useDb)
    {
        var conn = new MySqlConnection(_cfg.GetPressConnectionString(useDb));
        conn.Open();
        return conn;
    }

    /// <summary>DB·press_document 가 없으면 생성(첫 반입). 쓰기 권한 필요.</summary>
    public void EnsureSchema()
    {
        using (var conn = Open(useDb: false))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{_cfg.PressDbName}` " +
                              "CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
            cmd.ExecuteNonQuery();
        }
        using (var conn = Open(useDb: true))
        using (var cmd = conn.CreateCommand())
        {
            // stn-crawler sql/001_schema.sql 의 press_document 와 동일.
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS press_document (
  id             INT           NOT NULL AUTO_INCREMENT,
  source         ENUM('fsc','fss','moef','bok') NOT NULL,
  source_seq     VARCHAR(128)  NOT NULL,
  folder         VARCHAR(512)  NOT NULL,
  published_date DATE          DEFAULT NULL,
  post_title     VARCHAR(512)  DEFAULT NULL,
  file_name      VARCHAR(512)  NOT NULL,
  file_ext       VARCHAR(16)   DEFAULT NULL,
  file_url       VARCHAR(1024) DEFAULT NULL,
  content        MEDIUMTEXT    DEFAULT NULL,
  char_count     INT           NOT NULL DEFAULT 0,
  content_hash   CHAR(64)      DEFAULT NULL,
  crawled_at     TIMESTAMP     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at     TIMESTAMP     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  UNIQUE KEY uq_file (source, source_seq, file_name(255)),
  KEY idx_source (source),
  KEY idx_published (published_date),
  KEY idx_source_seq (source, source_seq),
  KEY idx_folder (folder(255))
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>이미 반입된 키 집합. press_document 가 없으면 빈 집합(첫 반입).</summary>
    public HashSet<(string, string, string)> LoadExistingKeys()
    {
        var set = new HashSet<(string, string, string)>();
        try
        {
            using var conn = Open(useDb: true);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT source, source_seq, file_name FROM press_document";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                set.Add((rdr.GetString(0), rdr.GetString(1), rdr.GetString(2)));
        }
        catch (MySqlException)
        {
            // DB/테이블 없음·접속 불가 → 첫 반입으로 간주(빈 집합). 실제 권한 오류는 반입 시 드러남.
        }
        return set;
    }

    /// <summary>
    /// 레코드를 UNIQUE(source, source_seq, file_name) 기준 upsert. 멱등.
    /// 스트리밍 입력 + batchCommit 마다 커밋해 거대 트랜잭션을 피한다.
    /// 중단 시 직전 커밋분까지 반영(멱등 재반입으로 이어서 완료 가능).
    /// 반환: (신규 삽입, 갱신).
    /// </summary>
    public (int Inserted, int Updated) Upsert(
        IEnumerable<PressFullRecord> records, int total,
        Action<int, int>? onProgress = null, CancellationToken ct = default,
        int batchCommit = 2000)
    {
        int inserted = 0, updated = 0, done = 0;
        using var conn = Open(useDb: true);
        var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO press_document
    (source, source_seq, folder, published_date, post_title, file_name, file_ext, file_url, content, char_count, content_hash)
VALUES (@s, @q, @fo, @pd, @pt, @fn, @fe, @fu, @c, @cc, @h)
ON DUPLICATE KEY UPDATE
    folder=VALUES(folder), published_date=VALUES(published_date), post_title=VALUES(post_title),
    file_ext=VALUES(file_ext), file_url=VALUES(file_url), content=VALUES(content),
    char_count=VALUES(char_count), content_hash=VALUES(content_hash)";
        var pS  = cmd.Parameters.Add("@s",  MySqlDbType.VarChar);
        var pQ  = cmd.Parameters.Add("@q",  MySqlDbType.VarChar);
        var pFo = cmd.Parameters.Add("@fo", MySqlDbType.VarChar);
        var pPd = cmd.Parameters.Add("@pd", MySqlDbType.Date);
        var pPt = cmd.Parameters.Add("@pt", MySqlDbType.VarChar);
        var pFn = cmd.Parameters.Add("@fn", MySqlDbType.VarChar);
        var pFe = cmd.Parameters.Add("@fe", MySqlDbType.VarChar);
        var pFu = cmd.Parameters.Add("@fu", MySqlDbType.VarChar);
        var pC  = cmd.Parameters.Add("@c",  MySqlDbType.MediumText);
        var pCc = cmd.Parameters.Add("@cc", MySqlDbType.Int32);
        var pH  = cmd.Parameters.Add("@h",  MySqlDbType.String);

        try
        {
            foreach (var r in records)
            {
                ct.ThrowIfCancellationRequested();
                pS.Value  = r.Source;
                pQ.Value  = r.SourceSeq;
                pFo.Value = r.Folder;
                pPd.Value = (object?)r.PublishedDate ?? DBNull.Value;
                pPt.Value = r.PostTitle ?? "";
                pFn.Value = r.FileName;
                pFe.Value = r.FileExt ?? "";
                pFu.Value = r.FileUrl ?? "";
                pC.Value  = (object?)r.Content ?? DBNull.Value;
                pCc.Value = r.CharCount;
                pH.Value  = (object?)r.ContentHash ?? DBNull.Value;

                var affected = cmd.ExecuteNonQuery();
                if (affected == 1) inserted++; else updated++;   // 1=삽입, 2=갱신, 0=동일(갱신으로 셈)
                done++;

                if (done % batchCommit == 0)
                {
                    tx.Commit(); tx.Dispose();
                    onProgress?.Invoke(done, total);
                    tx = conn.BeginTransaction();
                    cmd.Transaction = tx;
                }
            }
            tx.Commit();
        }
        finally { tx.Dispose(); }

        onProgress?.Invoke(total, total);
        return (inserted, updated);
    }
}
