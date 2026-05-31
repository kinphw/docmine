// HWP 배치 적재 — Python inserter.run 의 1:1 포팅.
//
// 핵심 동작:
//   1. HwpWorker.exe 자식 프로세스 spawn (Job Object 자동 상속)
//   2. CSV 의 .hwp/.hwpx 행만 처리, DB 기존 키 skip
//   3. 메인 루프: 한 행씩 worker stdin 으로 보내고 stdout 응답 대기
//   4. PARSE_TIMEOUT 초과 시 worker kill + 새 spawn (Python COM 크래시 회복 패턴)
//   5. 응답을 DB INSERT, commit-every batching
//   6. stop_event(CancellationToken) 받으면 정상 정리
//
// 자식 워커는 STDIO JSON 프로토콜 (Phase 4-2 참조).

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DocMine.Core.Config;
using DocMine.Core.Db;
using MySqlConnector;

namespace DocMine.Core.Pipeline;

public sealed record HwpInsertProgress(
    int Total, int Index, int Ok, int Err, int Skip, int Crash);

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

    public HwpInsertRunner(AppConfig cfg)
    {
        _cfg = cfg;
        _repo = new DocumentRepository(cfg);

        // 워커는 자기 자신(python.exe) 을 --hwp-worker 모드로 재실행.
        // Python mp.Process 가 같은 인터프리터 바이너리를 spawn 하는 패턴 1:1.
        _workerExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath null — 워커 spawn 불가");
    }

    public async Task<int> RunAsync(
        string csvPath,
        int start = 0,
        int? end = null,
        Action<string>? onLog = null,
        Action<HwpInsertProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var allRows = CsvIngestHelpers.LoadCsv(csvPath);
        var hwpRows = allRows
            .Where(r => HwpExts.Contains(r.Extension))
            .ToList();
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
        var knownKeys = CsvIngestHelpers.LoadExistingKeys(_cfg, _repo, rows);
        if (knownKeys.Count > 0)
            onLog?.Invoke($"  ✓ DB 기존 파일 {knownKeys.Count:N0}건은 파싱 없이 건너뜁니다.");

        var stats = new Stats(rows.Count);

        // ── skip 대상 bulk 제외 — 개별 Tick + onProgress 폭주 회피 (PDF 와 동일) ──
        var toProcess = new List<(int Idx, CsvRow Row)>();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (knownKeys.Contains(CsvIngestHelpers.NormKey(row.Directory, row.Filename))) continue;
            toProcess.Add((start + i, row));
        }
        var skipCount = rows.Count - toProcess.Count;
        if (skipCount > 0)
        {
            stats.TickSkipBulk(skipCount);
            onProgress?.Invoke(stats.Snapshot());
        }
        onLog?.Invoke($"  처리 대상: {toProcess.Count:N0}건 (skip {skipCount:N0}건 제외)");

        if (toProcess.Count == 0)
        {
            onLog?.Invoke("  모든 파일이 이미 DB 에 있습니다 — 파싱 없이 종료.");
            return 0;
        }

        // 시작 시점에 이전 실행의 좀비 Hwp.exe 정리 (Python 패턴).
        HwpComExtractor_ProcessKill();

        await using var conn = _repo.OpenConnection();
        await using var errLog = new StreamWriter("hwp_parse_errors.csv", append: true,
            new System.Text.UTF8Encoding(true));

        var worker = StartWorker(onLog);
        onLog?.Invoke($"  ✓ 워커 프로세스 시작 (PID {worker.Process.Id})");

        var pending = 0;

        try
        {
            foreach (var (globalIdx, row) in toProcess)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    onLog?.Invoke("\n  중지 요청 — 워커 정리 중…");
                    break;
                }

                var fp = Path.Combine(row.Directory, row.Filename);

                if (!File.Exists(fp))
                {
                    await InsertAsync(conn, row, null, "error", "파일 없음");
                    await WriteErrAsync(errLog, row, "파일 없음");
                    pending++;
                    stats.Tick("error");
                    onProgress?.Invoke(stats.Snapshot());
                    if (pending >= AppConfig.CommitEvery) { conn.Close(); conn.Open(); pending = 0; }
                    continue;
                }
                if (fp.Length > 260)
                {
                    var msg = $"경로 초과({fp.Length}자)";
                    await InsertAsync(conn, row, null, "error", msg);
                    await WriteErrAsync(errLog, row, msg);
                    pending++;
                    stats.Tick("error");
                    onProgress?.Invoke(stats.Snapshot());
                    if (pending >= AppConfig.CommitEvery) { conn.Close(); conn.Open(); pending = 0; }
                    continue;
                }

                // 워커에 요청 보내고 응답 대기.
                var req = new { op = "parse", idx = globalIdx, path = fp, ext = row.Extension.ToLowerInvariant() };
                var reqJson = JsonSerializer.Serialize(req, JsonOpts);

                ParseResponse? resp;
                try
                {
                    resp = await SendAndReceiveAsync(worker, reqJson,
                        TimeSpan.FromSeconds(AppConfig.ParseTimeoutSeconds), cancellationToken);
                }
                catch (TimeoutException)
                {
                    resp = null;  // 워커 죽이고 재시작
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    resp = new ParseResponse(globalIdx, "error", null, $"워커 통신 오류: {ex.Message}");
                }

                if (resp is null)
                {
                    // 타임아웃 / 워커 죽음 — 워커 kill + 새 spawn.
                    var errMsg = worker.Process.HasExited ? "워커 크래시" : "타임아웃/크래시";
                    try { worker.Process.Kill(entireProcessTree: true); } catch { }
                    HwpComExtractor_ProcessKill();
                    worker.Dispose();
                    Thread.Sleep(1000);
                    worker = StartWorker(onLog);
                    onLog?.Invoke($"\n  워커 재시작 (PID {worker.Process.Id}) — [{globalIdx}] {row.Filename}");

                    await WriteErrAsync(errLog, row, errMsg);
                    await InsertAsync(conn, row, null, "error", errMsg);
                    pending++;
                    stats.Tick("crash");
                    onProgress?.Invoke(stats.Snapshot());
                    if (pending >= AppConfig.CommitEvery) { conn.Close(); conn.Open(); pending = 0; }
                    continue;
                }

                if (!string.IsNullOrEmpty(resp.Err))
                    await WriteErrAsync(errLog, row, resp.Err);

                await InsertAsync(conn, row, resp.Text, resp.Status, resp.Err);
                pending++;
                stats.Tick(resp.Status);
                onProgress?.Invoke(stats.Snapshot());
                if (pending >= AppConfig.CommitEvery) { conn.Close(); conn.Open(); pending = 0; }
            }
        }
        finally
        {
            // 정상 종료 시도 — quit 보내고 응답 대기, 안 되면 kill.
            try
            {
                await worker.Process.StandardInput.WriteLineAsync("{\"op\":\"quit\"}");
                await worker.Process.StandardInput.FlushAsync();
                worker.Process.WaitForExit(3000);
            }
            catch { }
            try { if (!worker.Process.HasExited) worker.Process.Kill(entireProcessTree: true); } catch { }
            worker.Dispose();
            HwpComExtractor_ProcessKill();

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM `{_cfg.DbTable}`";
                var dbCount = Convert.ToInt32(cmd.ExecuteScalar());
                var matchStr = stats.Cur == rows.Count
                    ? "일치" : $"불일치 (처리 {stats.Cur} vs 대상 {rows.Count})";
                onLog?.Invoke("");
                onLog?.Invoke("  [건수 대조]");
                onLog?.Invoke($"    이번 배치 대상 : {rows.Count:N0}건");
                onLog?.Invoke($"    이번 배치 처리 : {stats.Cur:N0}건  {matchStr}");
                onLog?.Invoke($"    DB 전체 누적   : {dbCount:N0}건");
            }
            catch (Exception ex) { onLog?.Invoke($"  [통계 조회 실패] {ex.Message}"); }
        }

        onLog?.Invoke(stats.Summary());
        onLog?.Invoke("  에러 로그: hwp_parse_errors.csv");
        return 0;
    }

    // ─ Worker 관리 ──────────────────────────────────────────────────

    private WorkerHandle StartWorker(Action<string>? onLog)
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
        // 같은 binary 재실행 → --hwp-worker 모드 진입.
        psi.ArgumentList.Add("--hwp-worker");

        var p = Process.Start(psi)
            ?? throw new InvalidOperationException("HwpWorker spawn 실패");

        // stderr 는 별도 스레드에서 흡수 → 부모 로그로 전달.
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await p.StandardError.ReadLineAsync()) is not null)
                {
                    if (!string.IsNullOrWhiteSpace(line)) onLog?.Invoke($"  [worker] {line}");
                }
            }
            catch { }
        });
        return new WorkerHandle(p, stderrTask);
    }

    private static async Task<ParseResponse?> SendAndReceiveAsync(
        WorkerHandle worker, string reqJson, TimeSpan timeout, CancellationToken ct)
    {
        await worker.Process.StandardInput.WriteLineAsync(reqJson);
        await worker.Process.StandardInput.FlushAsync();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
        try
        {
            var line = await worker.Process.StandardOutput.ReadLineAsync(linked.Token);
            if (line is null) throw new IOException("워커 stdout EOF — 프로세스 종료된 듯");
            return JsonSerializer.Deserialize<ParseResponse>(line, JsonOpts);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static void HwpComExtractor_ProcessKill()
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
        catch (Exception) { /* 진행 막지 않음 */ }
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

    // ─ 워커 핸들 + 통계 ─────────────────────────────────────────────

    private sealed class WorkerHandle : IDisposable
    {
        public Process Process { get; }
        public Task StderrTask { get; }
        public WorkerHandle(Process p, Task stderrTask) { Process = p; StderrTask = stderrTask; }
        public void Dispose() { try { Process.Dispose(); } catch { } }
    }

    private sealed record ParseResponse(int Idx, string Status, string? Text, string? Err);

    private sealed class Stats
    {
        public int Cur, Ok, Err, Skip, Empty, Crash;
        private readonly int _total;
        public Stats(int total) => _total = total;
        public void Tick(string s)
        {
            Cur++;
            switch (s)
            {
                case "success": Ok++; break;
                case "error":   Err++; break;
                case "skip":    Skip++; break;
                case "empty":   Empty++; break;
                case "crash":   Crash++; break;
            }
        }
        public void TickSkipBulk(int n) { Cur += n; Skip += n; }
        public HwpInsertProgress Snapshot() => new(_total, Cur, Ok, Err, Skip, Crash);
        public string Summary()
        {
            var empty = Empty > 0 ? $" empty:{Empty}" : "";
            var skip  = Skip  > 0 ? $" skip:{Skip}"   : "";
            var crash = Crash > 0 ? $" crash:{Crash}" : "";
            return $"  완료: ok:{Ok} err:{Err}{empty}{skip}{crash}";
        }
    }
}
