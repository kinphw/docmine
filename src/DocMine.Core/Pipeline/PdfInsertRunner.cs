// PDF 배치 파싱 → MariaDB — Python pdf_inserter.run 의 1:1 포팅.
//
// 설계 (Python 판과 동등한 *프로세스* 격리):
//   - PdfPig 가 매니지드라도 큰/손상된 PDF 에서 OOM/AccessViolation 시 process
//     통째 종료. Parallel.ForEachAsync (thread 격리) 로는 메인 GUI 까지 silent
//     종료되는 회귀 발견.  Python mp.Pool 처럼 워커 *프로세스* N개 spawn 으로
//     완전 격리 — 한 워커가 죽어도 메인 + 다른 워커 살아남고 새 워커 spawn.
//   - 메인 = 코디네이터. 각 워커 = "한 작업씩 받아 응답" 의 STDIO JSON 루프.
//   - 작업은 라운드로빈으로 N 워커에 분배. 각 워커는 자기 큐를 직렬 처리.
//   - 한 워커 crash 시 새 워커 spawn 후 그 워커는 자기 남은 작업 계속.
//   - 메인은 결과 채널을 받아 DB INSERT 만 (직렬, 단일 커넥션).
//   - 본문이 빈 PDF 는 parse_status='empty' — 진짜 error 와 구분.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DocMine.Core.Config;
using DocMine.Core.Db;
using DocMine.Core.Pdf;
using MySqlConnector;

namespace DocMine.Core.Pipeline;

// CsvRow 는 CsvIngestHelpers.cs 로 이동 (HWP/PDF 공유).

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
    private readonly string _workerExePath;

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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public PdfInsertRunner(AppConfig cfg)
    {
        _cfg = cfg;
        _repo = new DocumentRepository(cfg);
        // 워커는 자기 자신(python.exe) 을 --pdf-worker 모드로 재실행.
        _workerExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath null — PDF 워커 spawn 불가");
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
        var allRows = CsvIngestHelpers.LoadCsv(csvPath);
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
        var knownKeys = CsvIngestHelpers.LoadExistingKeys(_cfg, _repo, rows);
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
        onLog?.Invoke($"  PDF 파서 워커 프로세스 {workers}개로 병렬 파싱 (논리 CPU {cpu}개)…");

        // 작업을 워커 수로 라운드로빈 분배 — 각 워커 = 자기 큐를 직렬 처리.
        var queues = new List<List<(int Idx, string Path, CsvRow Row)>>();
        for (int i = 0; i < workers; i++) queues.Add(new List<(int, string, CsvRow)>());
        for (int i = 0; i < tasks.Count; i++) queues[i % workers].Add(tasks[i]);

        // 결과 채널 — 모든 워커가 push, 메인 INSERT 루프가 직렬 pull.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<WorkerResult>();

        // 메인 INSERT 루프.
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

        // 워커별 처리 task — N 개 동시 실행.
        var workerTasks = queues.Select((queue, idx) => Task.Run(async () =>
        {
            Process? worker = StartPdfWorker(onLog, idx);
            try
            {
                foreach (var t in queue)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    // 워커 죽었으면 새로 spawn.
                    if (worker is null || worker.HasExited)
                    {
                        if (worker is not null)
                        {
                            onLog?.Invoke($"\n  [worker {idx}] 죽음 감지 — 새 워커 spawn");
                            try { worker.Dispose(); } catch { }
                        }
                        worker = StartPdfWorker(onLog, idx);
                    }

                    CrashTrace($"[{t.Idx}] >>> {t.Path}");
                    WorkerResult result;
                    try
                    {
                        var req = new { op = "parse", idx = t.Idx, path = t.Path };
                        var reqJson = JsonSerializer.Serialize(req, JsonOpts);
                        await worker.StandardInput.WriteLineAsync(reqJson);
                        await worker.StandardInput.FlushAsync();

                        var line = await worker.StandardOutput.ReadLineAsync(cancellationToken);
                        if (line is null)
                            throw new IOException("워커 stdout EOF — 처리 중 죽음 (큰 PDF 의 OOM 가능성)");
                        var resp = JsonSerializer.Deserialize<WorkerResponse>(line, JsonOpts)!;
                        result = new WorkerResult(t.Row, resp.Status, resp.Text, resp.Err);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // 워커 crash 또는 통신 오류 — 이 작업은 error 처리, 다음 작업 위해 새 워커 spawn.
                        var msg = $"워커 crash: {ex.Message}";
                        if (msg.Length > 900) msg = msg[..900];
                        result = new WorkerResult(t.Row, "error", null, msg);
                        try { if (worker is not null && !worker.HasExited) worker.Kill(entireProcessTree: true); } catch { }
                        try { worker?.Dispose(); } catch { }
                        worker = null;
                    }
                    CrashTrace($"[{t.Idx}] <<< {result.Status}");
                    await channel.Writer.WriteAsync(result, cancellationToken);
                }
            }
            finally
            {
                // 정상 종료 — quit 보내고 응답 대기, 안 되면 kill.
                if (worker is not null && !worker.HasExited)
                {
                    try { await worker.StandardInput.WriteLineAsync("{\"op\":\"quit\"}"); await worker.StandardInput.FlushAsync(); } catch { }
                    try { worker.WaitForExit(2000); } catch { }
                    try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); } catch { }
                }
                try { worker?.Dispose(); } catch { }
            }
        }, cancellationToken)).ToList();

        try
        {
            await Task.WhenAll(workerTasks);
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
    private sealed record WorkerResponse(int Idx, string Status, string? Text, string? Err);

    /// <summary>같은 binary 를 --pdf-worker 모드로 spawn. stdin/stdout/stderr pipe redirect.</summary>
    private Process StartPdfWorker(Action<string>? onLog, int workerIdx)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _workerExePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_workerExePath),
        };
        psi.ArgumentList.Add("--pdf-worker");

        var p = Process.Start(psi) ?? throw new InvalidOperationException("PdfWorker spawn 실패");

        // stderr 흡수 → 부모 로그.
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await p.StandardError.ReadLineAsync()) is not null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        onLog?.Invoke($"  [pdf-worker {workerIdx}] {line}");
                }
            }
            catch { }
        });
        return p;
    }

    // ─ CSV / DB 헬퍼는 CsvIngestHelpers.cs 로 이동 (HWP/PDF 공유) ────

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

    // ─ Crash trace — silent crash 시 마지막 처리 파일 추적 ─────────
    //
    // PdfPig 가 특정 PDF 에서 native-level access violation 등으로 process 통째
    // 종료되면 try/catch 가 못 잡고 UI 메시지도 못 띄움. 그 경우 이 로그의
    // ">>> " 로 끝난 마지막 줄이 죽인 파일.  운영 중 박건영님이 crash 후 확인.
    //
    // 8 worker 동시 호출이라 lock 으로 직렬화. WriteAllText 가 매번 flush.
    private const string CrashLogFile = "pdf_crash_trace.log";
    private static readonly object CrashLogLock = new();

    private static void CrashTrace(string msg)
    {
        try
        {
            lock (CrashLogLock)
            {
                File.AppendAllText(CrashLogFile,
                    $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { /* 진단 로그가 실패해도 본 동작에 영향 없게 */ }
    }

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
