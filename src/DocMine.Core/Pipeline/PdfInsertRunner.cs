// PDF 배치 파싱 → MariaDB — Python pdf_inserter.run 의 1:1 포팅.
//
// 설계 메모 (Python 판과 동일 의도):
//   - 본문 텍스트 추출은 CPU-바운드 → Parallel.ForEachAsync 로 병렬 파싱.
//     (Python 의 mp.Pool.imap_unordered 등가)
//   - 메인 스레드는 결과 채널을 받아 DB INSERT 만 수행(직렬, 단일 커넥션).
//   - 본문이 빈 PDF(스캔본/이미지) 는 parse_status='empty' 로 표기 —
//     진짜 파싱 실패('error') 와 명시적으로 구분.
//   - 사전 점검(파일 존재, DB skip) 은 직렬로 — 워커 spawn 비용 절약.
//   - stop_event 등가는 CancellationToken — 메인 루프와 워커 둘 다 받음.

using System.Globalization;
using DocMine.Core.Config;
using DocMine.Core.Db;
using DocMine.Core.Pdf;
using MySqlConnector;

namespace DocMine.Core.Pipeline;

public sealed record CsvRow(
    string Directory,
    string Filename,
    string Extension,
    long   SizeBytes,
    string Modified);

public sealed record PdfInsertProgress(
    int Total,    // 처리 대상 (CSV 의 PDF 행 총수)
    int Index,    // 현재 처리 완료 누적
    int Ok,
    int Err,
    int Skip,
    int Empty);

public sealed class PdfInsertRunner
{
    private readonly AppConfig _cfg;
    private readonly DocumentRepository _repo;

    // 본문이 비어 있을 때의 메시지 — 'error' 가 아니라 'empty' 상태로 기록.
    private const string EmptyTextMsg = "본문 텍스트 없음 (스캔본/이미지 PDF — OCR 미적용)";

    private const string InsertSql = @"
INSERT INTO `{0}`
    (directory, filename, extension, file_size, file_mtime,
     body_text, parse_status, error_msg)
VALUES (@d, @fn, @ext, @sz, @mt, @body, @status, @err)
ON DUPLICATE KEY UPDATE
    body_text=VALUES(body_text), parse_status=VALUES(parse_status),
    error_msg=VALUES(error_msg), parsed_at=CURRENT_TIMESTAMP";

    public PdfInsertRunner(AppConfig cfg)
    {
        _cfg = cfg;
        _repo = new DocumentRepository(cfg);
    }

    /// <summary>
    /// CSV → PDF 본문 파싱 → DB 적재.
    /// </summary>
    /// <param name="onLog">진행 로그 한 줄. UI 의 LogPane.AppendLine 등에 연결.</param>
    /// <param name="onProgress">파일 1건 처리 후마다 누적 통계 콜백.</param>
    public async Task<int> RunAsync(
        string csvPath,
        int start = 0,
        int? end = null,
        Action<string>? onLog = null,
        Action<PdfInsertProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        // JobObject 셋업은 UI 진입점(Program.Main) 에서 한 번 처리 — 여기선 무관.
        var allRows = LoadCsv(csvPath);
        var pdfRows = allRows
            .Where(r => string.Equals(r.Extension, ".pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var endIdx = end ?? pdfRows.Count;
        var rows = pdfRows.Skip(start).Take(endIdx - start).ToList();

        onLog?.Invoke($"  CSV 전체: {allRows.Count:N0}건 (그 중 PDF {pdfRows.Count:N0}건)");
        onLog?.Invoke($"  처리 범위: [{start}:{endIdx}] → {rows.Count:N0}건");

        if (rows.Count == 0)
        {
            onLog?.Invoke("  처리할 PDF 가 없습니다.");
            return 0;
        }

        _repo.EnsureDatabase();
        var knownKeys = LoadExistingKeys(rows);
        if (knownKeys.Count > 0)
            onLog?.Invoke($"  ✓ DB 기존 파일 {knownKeys.Count:N0}건은 파싱 없이 건너뜁니다.");

        // ── 1) 사전 점검 (직렬) — DB skip / 파일 없음 ──
        var tasks = new List<(int Idx, string Path, CsvRow Row)>();
        var stats = new Stats(rows.Count);
        var pendingCommit = 0;

        await using var conn = _repo.OpenConnection();
        await using var errLogStream = new StreamWriter(
            "pdf_parse_errors.csv", append: true,
            new System.Text.UTF8Encoding(true));

        for (var i = 0; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[i];
            var fp = Path.Combine(row.Directory, row.Filename);

            if (knownKeys.Contains((row.Directory, row.Filename)))
            {
                stats.Tick("skip");
                onProgress?.Invoke(stats.Snapshot());
                continue;
            }
            if (!File.Exists(PdfTextExtractor.WinLongPath(fp)))
            {
                await InsertAsync(conn, row, null, "error", "파일 없음");
                await WriteErrAsync(errLogStream, row, "파일 없음");
                pendingCommit++;
                stats.Tick("error");
                onProgress?.Invoke(stats.Snapshot());
                if (pendingCommit >= AppConfig.CommitEvery) { conn.Close(); conn.Open(); pendingCommit = 0; }
                continue;
            }
            tasks.Add((i, fp, row));
        }

        // ── 2) 병렬 파싱 + 결과 채널 → 메인에서 INSERT ──
        if (tasks.Count == 0 || cancellationToken.IsCancellationRequested)
            goto Finalize;

        var workers = Math.Max(1, Math.Min(AppConfig.PdfWorkers, tasks.Count));
        var cpu = Environment.ProcessorCount;
        onLog?.Invoke($"  PDF 파서 워커 {workers}개로 병렬 파싱 (논리 CPU {cpu}개)…");

        // 워커가 결과를 던지는 채널. 메인 루프가 await 로 받아 직렬 INSERT.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<WorkerResult>();

        // 메인 INSERT 루프 — 채널 읽으면서 진행.
        var insertTask = Task.Run(async () =>
        {
            await foreach (var r in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (!string.IsNullOrEmpty(r.ErrMsg) && r.Status != "empty")
                    await WriteErrAsync(errLogStream, r.Row, r.ErrMsg!);
                await InsertAsync(conn, r.Row, r.Text, r.Status, r.ErrMsg);
                pendingCommit++;
                stats.Tick(r.Status);
                onProgress?.Invoke(stats.Snapshot());
                if (pendingCommit >= AppConfig.CommitEvery) { conn.Close(); conn.Open(); pendingCommit = 0; }
            }
        }, cancellationToken);

        // 워커 — Parallel.ForEachAsync.
        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = workers,
            CancellationToken = cancellationToken,
        };
        try
        {
            await Parallel.ForEachAsync(tasks, parallelOpts, async (t, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                string? text = null;
                string status = "success";
                string? errMsg = null;
                try
                {
                    text = PdfTextExtractor.Extract(t.Path);
                    if (string.IsNullOrEmpty(text))
                    {
                        status = "empty";
                        text = null;
                        errMsg = EmptyTextMsg;
                    }
                }
                catch (Exception ex)
                {
                    status = "error";
                    text = null;
                    errMsg = Truncate(ex.Message, 900);
                }
                await channel.Writer.WriteAsync(
                    new WorkerResult(t.Row, status, text, errMsg), ct);
            });
        }
        catch (OperationCanceledException)
        {
            onLog?.Invoke("\n  중지 요청 — 워커 종료 중…");
        }
        channel.Writer.Complete();
        try { await insertTask; } catch (OperationCanceledException) { }

    Finalize:
        // ── 마무리 ──
        try
        {
            using (var cur = conn.CreateCommand())
            {
                cur.CommandText = $"SELECT COUNT(*) FROM `{_cfg.DbTable}`";
                var dbCount = Convert.ToInt32(cur.ExecuteScalar());
                var matchStr = stats.Cur == rows.Count
                    ? "일치"
                    : $"불일치 (처리 {stats.Cur} vs 대상 {rows.Count})";
                onLog?.Invoke("");
                onLog?.Invoke("  [건수 대조]");
                onLog?.Invoke($"    이번 배치 대상 : {rows.Count:N0}건");
                onLog?.Invoke($"    이번 배치 처리 : {stats.Cur:N0}건  {matchStr}");
                onLog?.Invoke($"    DB 전체 누적   : {dbCount:N0}건 (HWP + PDF 합산)");
            }
        }
        catch (Exception ex) { onLog?.Invoke($"  [통계 조회 실패] {ex.Message}"); }

        onLog?.Invoke(stats.Summary());
        onLog?.Invoke("  에러 로그: pdf_parse_errors.csv");
        onLog?.Invoke("  (스캔본/이미지 PDF 는 'empty' 로 분류 — 에러 아님)");
        return 0;
    }

    private record WorkerResult(CsvRow Row, string Status, string? Text, string? ErrMsg);

    // ─ CSV / DB 헬퍼 ─────────────────────────────────────────────────

    private static List<CsvRow> LoadCsv(string csvPath)
    {
        var rows = new List<CsvRow>();
        using var reader = new StreamReader(csvPath, new System.Text.UTF8Encoding(true));
        var header = reader.ReadLine();
        if (header is null) return rows;

        // Python csv.DictReader 등가 — 컬럼 인덱스 매핑.
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

    // RFC 4180 의 최소 구현 — 따옴표 안의 콤마/이중따옴표만 처리.
    // DriveScanner.CsvEscape 가 만든 CSV 라 단순 케이스만 필요.
    private static List<string> ParseCsvLine(string line)
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

    private HashSet<(string, string)> LoadExistingKeys(List<CsvRow> rows)
    {
        var keys = rows
            .Where(r => !string.IsNullOrEmpty(r.Directory) && !string.IsNullOrEmpty(r.Filename))
            .Select(r => (r.Directory, r.Filename))
            .ToHashSet();
        if (keys.Count == 0) return keys;

        var existing = new HashSet<(string, string)>();
        const int chunkSize = 500;
        var keyList = keys.ToList();
        using var conn = _repo.OpenConnection();
        for (var i = 0; i < keyList.Count; i += chunkSize)
        {
            var chunk = keyList.Skip(i).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select((_, j) => $"(@d{j}, @f{j})"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT directory, filename FROM `{_cfg.DbTable}` " +
                $"WHERE (directory, filename) IN ({placeholders})";
            for (var j = 0; j < chunk.Count; j++)
            {
                cmd.Parameters.AddWithValue($"@d{j}", chunk[j].Directory);
                cmd.Parameters.AddWithValue($"@f{j}", chunk[j].Filename);
            }
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                existing.Add((rdr.GetString(0), rdr.GetString(1)));
        }
        return existing;
    }

    private async Task InsertAsync(MySqlConnection conn, CsvRow row, string? text, string status, string? errMsg)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = string.Format(CultureInfo.InvariantCulture, InsertSql, _cfg.DbTable);
            cmd.Parameters.AddWithValue("@d", row.Directory);
            cmd.Parameters.AddWithValue("@fn", row.Filename);
            cmd.Parameters.AddWithValue("@ext", row.Extension.ToLowerInvariant());
            cmd.Parameters.AddWithValue("@sz", row.SizeBytes);
            cmd.Parameters.AddWithValue("@mt", row.Modified);
            cmd.Parameters.AddWithValue("@body", (object?)text ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@err", (object?)errMsg ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception) { /* 진행 막지 않음 — 에러 로그는 별도 */ }
    }

    private static async Task WriteErrAsync(StreamWriter w, CsvRow row, string msg)
    {
        // 단순 CSV — 운영 환경에서 사후 점검용.
        await w.WriteLineAsync($"{CsvEscape(row.Directory)},{CsvEscape(row.Filename)},{CsvEscape(msg)}");
        await w.FlushAsync();
    }

    private static string CsvEscape(string s)
        => s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
            ? s
            : "\"" + s.Replace("\"", "\"\"") + "\"";

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];

    // ─ 통계 ──────────────────────────────────────────────────────────
    private sealed class Stats
    {
        public int Cur;
        public int Ok, Err, Skip, Empty;
        private readonly int _total;
        public Stats(int total) => _total = total;

        public void Tick(string status)
        {
            Cur++;
            switch (status)
            {
                case "success": Ok++; break;
                case "error":   Err++; break;
                case "skip":    Skip++; break;
                case "empty":   Empty++; break;
            }
        }

        public PdfInsertProgress Snapshot() => new(_total, Cur, Ok, Err, Skip, Empty);

        public string Summary()
        {
            var empty = Empty > 0 ? $" empty:{Empty}" : "";
            var skip  = Skip  > 0 ? $" skip:{Skip}"   : "";
            return $"  완료: ok:{Ok} err:{Err}{empty}{skip}";
        }
    }
}
