// HWP 배치 적재 — 멀티 워커 병렬 (PdfInsertRunner 와 동형 구조).
//
// 설계:
//   - 워커 프로세스 N 개(= 배분된 코어 예산, 대상 건수로 캡) 를 라운드로빈 큐로 분배.
//   - 각 워커 = --hwp-worker STDIO JSON 루프. 자기 큐를 직렬 처리.
//   - 메인 = 코디네이터. 결과 채널을 받아 DB INSERT 만(직렬, 단일 커넥션).
//   - 워커가 죽거나(EOF) 타임아웃(COM 폴백이 한글에서 멈춤)하면 그 워커만 kill+respawn.
//
// HWP 본문 추출은 이제 워커 안에서 매니지드(HwpBinaryReader) 우선 — COM/한글은
// 배포용·구형·파싱실패 시 폴백뿐이라 대부분 CPU 바운드다. 그래서 단일 COM 싱글턴에
// 묶이던 옛 직렬 구조를 벗고 PDF 처럼 병렬화할 수 있게 됐다.
//
// 멀티워커 COM 폴백 주의:
//   - 각 워커는 자기 HwpComExtractor 를 가진다(별도 프로세스).
//   - 워커는 --keep-hwp 로 띄워 서로의 한글(Hwp.exe)을 죽이지 않게 한다(크로스-워커 kill 방지).
//   - 좀비 한글 정리는 러너가 워커 없는 시점(시작 전 / 전원 종료 후)에만 전역 kill.
//
// DRM 불변식: 메인은 파일 *내용* 을 절대 읽지 않는다(메타데이터만). 본문 read 는 워커에서만.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DocMine.Core.Config;
using DocMine.Core.Db;
using MySqlConnector;

namespace DocMine.Core.Pipeline;

public sealed record HwpInsertProgress(
    int Total, int Index, int Ok, int Err, int Crash);

public sealed class HwpInsertRunner
{
    private static readonly HashSet<string> HwpExts =
        new(StringComparer.OrdinalIgnoreCase) { ".hwp", ".hwpx" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string InsertSql = @"
INSERT INTO `{0}`
    (directory, filename, extension, file_size, file_mtime,
     body_text, parse_status, error_msg)
VALUES (@d, @fn, @ext, @sz, @mt, @body, @status, @err)
ON DUPLICATE KEY UPDATE
    body_text=VALUES(body_text), parse_status=VALUES(parse_status),
    error_msg=VALUES(error_msg), parsed_at=CURRENT_TIMESTAMP";

    private readonly AppConfig _cfg;
    private readonly DocumentRepository _repo;
    private readonly string _workerExePath;
    private Action<string>? _onLog;

    public HwpInsertRunner(AppConfig cfg)
    {
        _cfg = cfg;
        _repo = new DocumentRepository(cfg);
        // 워커는 자기 자신(python.exe) 을 --hwp-worker 모드로 재실행.
        _workerExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath null — 워커 spawn 불가");
    }

    public async Task<int> RunAsync(
        string csvPath,
        int start = 0,
        int? end = null,
        Action<string>? onLog = null,
        Action<HwpInsertProgress>? onProgress = null,
        CancellationToken cancellationToken = default,
        bool retryErrors = false,
        int? maxWorkers = null)
    {
        _onLog = onLog;
        var allRows = CsvIngestHelpers.LoadCsv(csvPath);
        var hwpRows = allRows.Where(r => HwpExts.Contains(r.Extension)).ToList();
        var endIdx = end ?? hwpRows.Count;
        var rows = hwpRows.Skip(start).Take(endIdx - start).ToList();

        onLog?.Invoke($"  CSV 전체: {allRows.Count:N0}건 (그 중 HWP/HWPX {hwpRows.Count:N0}건)");
        onLog?.Invoke($"  처리 범위: [{start}:{endIdx}] → {rows.Count:N0}건");

        if (rows.Count == 0)
        {
            onLog?.Invoke("  처리할 파일이 없습니다.");
            return 0;
        }

        _repo.EnsureDatabase();
        // 상태별 기적재 판정 — retryErrors 면 error 행은 '기적재' 에서 빠져 재파싱된다.
        var statuses = CsvIngestHelpers.LoadKeyStatuses(_cfg, _repo, rows);
        var knownKeys = new HashSet<(string, string)>();
        var errorRetry = 0;
        foreach (var (key, status) in statuses)
        {
            if (retryErrors && status == "error") { errorRetry++; continue; }
            knownKeys.Add(key);
        }
        if (knownKeys.Count > 0)
            onLog?.Invoke($"  ✓ DB 기존 파일 {knownKeys.Count:N0}건은 파싱 없이 건너뜁니다.");
        if (errorRetry > 0)
            onLog?.Invoke($"  ↻ error 상태 {errorRetry:N0}건은 재적재(재파싱) 대상입니다.");

        // ── 기적재 제외 — 진행 분모는 '미적재(처리 대상)' 기준 ──
        var toProcess = new List<(int Idx, CsvRow Row)>();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (knownKeys.Contains(CsvIngestHelpers.NormKey(row.Directory, row.Filename))) continue;
            toProcess.Add((start + i, row));
        }
        var skipCount = rows.Count - toProcess.Count;
        onLog?.Invoke($"  처리 대상(미적재): {toProcess.Count:N0}건 · 기적재 {skipCount:N0}건 건너뜀");

        var stats = new Stats(toProcess.Count);
        onProgress?.Invoke(stats.Snapshot());   // 진행바 0/N 초기 표시

        // 작업 리스트 (경로·확장자). 파일 존재 점검은 워커 단계로 미룬다(메타데이터 — DRM 안전).
        var tasks = new List<TaskItem>(toProcess.Count);
        foreach (var (idx, row) in toProcess)
            tasks.Add(new TaskItem(idx, Path.Combine(row.Directory, row.Filename),
                                   row.Extension.ToLowerInvariant(), row));

        var pendingCommit = 0;

        await using var conn = _repo.OpenConnection();
        await using var errLog = new StreamWriter("hwp_parse_errors.csv", append: true,
            new System.Text.UTF8Encoding(true));

        if (tasks.Count == 0 || cancellationToken.IsCancellationRequested)
            goto Finalize;

        // 이전 실행의 좀비 한글 정리 (워커 spawn 전 — 안전 시점).
        KillHwpZombies();

        var workers = Math.Max(1, Math.Min(maxWorkers ?? Environment.ProcessorCount, tasks.Count));
        onLog?.Invoke($"  파서 워커 {workers}개 병렬 (배분 예산 {(maxWorkers ?? Environment.ProcessorCount)}개 / 논리 CPU {Environment.ProcessorCount}개)…");

        // 라운드로빈 분배 — 각 워커 = 자기 큐를 직렬 처리.
        var queues = new List<List<TaskItem>>();
        for (int i = 0; i < workers; i++) queues.Add(new List<TaskItem>());
        for (int i = 0; i < tasks.Count; i++) queues[i % workers].Add(tasks[i]);

        // 결과 채널 — bounded backpressure (직렬 INSERT 가 병렬 파싱을 못 따라가면 OOM 방지).
        var channel = System.Threading.Channels.Channel.CreateBounded<WorkerResult>(
            new System.Threading.Channels.BoundedChannelOptions(Math.Max(4, workers * 2))
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
                SingleReader = true,
            });

        // 메인 INSERT 루프.
        var insertTask = Task.Run(async () =>
        {
            await foreach (var r in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (!string.IsNullOrEmpty(r.ErrMsg))
                    await WriteErrAsync(errLog, r.Row, r.ErrMsg!);
                await InsertAsync(conn, r.Row, r.Text, r.Status, r.ErrMsg);
                pendingCommit++;
                stats.Tick(r.Status);
                onProgress?.Invoke(stats.Snapshot());
                if (pendingCommit >= AppConfig.CommitEvery) { conn.Close(); conn.Open(); pendingCommit = 0; }
            }
        }, cancellationToken);

        // 워커별 처리 task — N 개 동시.
        var workerTasks = queues.Select((queue, idx) => Task.Run(async () =>
        {
            Process? worker = StartWorker(idx);
            try
            {
                foreach (var t in queue)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    // 메타데이터 점검 (DRM 안전). \\?\ 롱패스는 DRM 복호화 후킹을 건너뛰므로
                    // 260 초과는 prefix 없이 error 처리(우회 안 함).
                    if (t.Path.Length > 260)
                    {
                        await channel.Writer.WriteAsync(
                            new WorkerResult(t.Row, "error", null, $"경로 초과({t.Path.Length}자)"), cancellationToken);
                        continue;
                    }
                    if (!File.Exists(t.Path))
                    {
                        await channel.Writer.WriteAsync(
                            new WorkerResult(t.Row, "error", null, "파일 없음"), cancellationToken);
                        continue;
                    }

                    if (worker is null || worker.HasExited)
                    {
                        if (worker is not null)
                        {
                            onLog?.Invoke($"\n  [worker {idx}] 죽음 감지 — 새 워커 spawn");
                            try { worker.Dispose(); } catch { }
                        }
                        worker = StartWorker(idx);
                    }

                    WorkerResult result;
                    try
                    {
                        var req = new { op = "parse", idx = t.Idx, path = t.Path, ext = t.Ext };
                        var reqJson = JsonSerializer.Serialize(req, JsonOpts);
                        await worker.StandardInput.WriteLineAsync(reqJson);
                        await worker.StandardInput.FlushAsync();

                        // per-요청 타임아웃 — COM 폴백이 한글에서 멈추는 경우 대비.
                        using var toCts = new CancellationTokenSource(
                            TimeSpan.FromSeconds(AppConfig.ParseTimeoutSeconds));
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken, toCts.Token);

                        string? line;
                        try { line = await worker.StandardOutput.ReadLineAsync(linked.Token); }
                        catch (OperationCanceledException) when (toCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        { throw new TimeoutException("파싱 타임아웃"); }

                        if (line is null)
                            throw new IOException("워커 stdout EOF — 처리 중 죽음");
                        var resp = JsonSerializer.Deserialize<WorkerResponse>(line, JsonOpts)!;
                        result = new WorkerResult(t.Row, resp.Status, resp.Text, resp.Err);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // 타임아웃/크래시 — 이 파일은 crash 처리, 워커 kill 후 다음 파일용 새 워커.
                        var msg = ex is TimeoutException ? "타임아웃/크래시" : $"워커 crash: {ex.Message}";
                        if (msg.Length > 900) msg = msg[..900];
                        result = new WorkerResult(t.Row, "crash", null, msg);
                        try { if (worker is not null && !worker.HasExited) worker.Kill(entireProcessTree: true); } catch { }
                        try { worker?.Dispose(); } catch { }
                        worker = null;
                    }
                    await channel.Writer.WriteAsync(result, cancellationToken);
                }
            }
            finally
            {
                if (worker is not null && !worker.HasExited)
                {
                    try { await worker.StandardInput.WriteLineAsync("{\"op\":\"quit\"}"); await worker.StandardInput.FlushAsync(); } catch { }
                    try { worker.WaitForExit(2000); } catch { }
                    try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); } catch { }
                }
                try { worker?.Dispose(); } catch { }
            }
        }, cancellationToken)).ToList();

        try { await Task.WhenAll(workerTasks); }
        catch (OperationCanceledException) { onLog?.Invoke("\n  중지 요청 — 워커 종료 중…"); }
        channel.Writer.Complete();
        try { await insertTask; } catch (OperationCanceledException) { }

        KillHwpZombies();   // COM 폴백이 남긴 한글 정리 (전원 종료 후 — 안전 시점).

    Finalize:
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM `{_cfg.DbTable}`";
            var dbCount = Convert.ToInt32(cmd.ExecuteScalar());
            var matchStr = stats.Cur == toProcess.Count
                ? "일치" : $"불일치 (처리 {stats.Cur} vs 대상 {toProcess.Count})";
            onLog?.Invoke("");
            onLog?.Invoke("  [건수 대조]");
            onLog?.Invoke($"    대상(미적재)  : {toProcess.Count:N0}건");
            onLog?.Invoke($"    기적재 건너뜀 : {skipCount:N0}건");
            onLog?.Invoke($"    처리 완료     : {stats.Cur:N0}건  {matchStr}");
            onLog?.Invoke($"    DB 전체 누적  : {dbCount:N0}건 (HWP + PDF 합산)");
        }
        catch (Exception ex) { onLog?.Invoke($"  [통계 조회 실패] {ex.Message}"); }

        onLog?.Invoke(stats.Summary());
        onLog?.Invoke("  에러 로그: hwp_parse_errors.csv");
        return 0;
    }

    // ─ Worker 관리 ──────────────────────────────────────────────────

    // --keep-hwp: 멀티워커에서 워커가 서로의 한글을 죽이지 않게 한다(좀비 정리는 러너가 전담).
    private Process StartWorker(int workerIdx)
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
        psi.ArgumentList.Add("--hwp-worker");
        psi.ArgumentList.Add("--keep-hwp");

        var p = Process.Start(psi) ?? throw new InvalidOperationException("HwpWorker spawn 실패");

        // stderr 흡수만 — 워커가 여럿이라 LogPane 으로 흘리면 화면이 어지럽고 UI thread 도 막힌다.
        // buffer deadlock 방지 위해 끝까지 읽되 출력 안 함.
        _ = Task.Run(async () => { try { await p.StandardError.ReadToEndAsync(); } catch { } });
        return p;
    }

    private static void KillHwpZombies()
    {
        foreach (var name in new[] { "Hwp", "HwpFrame" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    try { p.Dispose(); } catch { }
                }
            }
            catch { }
        }
    }

    // ─ DB INSERT ────────────────────────────────────────────────────

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
        catch (Exception ex)
        {
            _onLog?.Invoke($"  ⚠ INSERT 실패 — {row.Filename}: {ex.Message}");
        }
    }

    private static async Task WriteErrAsync(StreamWriter w, CsvRow row, string msg)
    {
        await w.WriteLineAsync($"{CsvEscape(row.Directory)},{CsvEscape(row.Filename)},{CsvEscape(msg)}");
        await w.FlushAsync();
    }

    private static string CsvEscape(string s)
        => s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
            ? s
            : "\"" + s.Replace("\"", "\"\"") + "\"";

    // ─ 레코드 · 통계 ─────────────────────────────────────────────────

    private readonly record struct TaskItem(int Idx, string Path, string Ext, CsvRow Row);
    private sealed record WorkerResult(CsvRow Row, string Status, string? Text, string? ErrMsg);
    private sealed record WorkerResponse(int Idx, string Status, string? Text, string? Err);

    private sealed class Stats
    {
        public int Cur, Ok, Err, Empty, Crash;
        private readonly int _total;   // 처리 대상(미적재) 수 — 기적재 미포함
        public Stats(int total) => _total = total;
        public void Tick(string s)
        {
            Cur++;
            switch (s)
            {
                case "success": Ok++; break;
                case "error":   Err++; break;
                case "empty":   Empty++; break;
                case "crash":   Crash++; break;
            }
        }
        public HwpInsertProgress Snapshot() => new(_total, Cur, Ok, Err, Crash);
        public string Summary()
        {
            var empty = Empty > 0 ? $" empty:{Empty}" : "";
            var crash = Crash > 0 ? $" crash:{Crash}" : "";
            return $"  완료: ok:{Ok} err:{Err}{empty}{crash}";
        }
    }
}
