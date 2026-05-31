// PDF 배치 파싱 → MariaDB.
//
// 설계 (Python 판과 동등한 *프로세스* 격리):
//   - 큰/손상된 PDF 에서 워커가 죽어도 메인 GUI 까지 함께 죽지 않도록 워커
//     *프로세스* N 개 (논리 CPU 수) 격리 — 한 워커 죽으면 새 워커 spawn.
//   - 메인 = 코디네이터. 각 워커 = STDIO JSON 루프 (한 작업씩 받아 응답).
//   - 작업은 라운드로빈으로 N 워커에 분배. 각 워커는 자기 큐를 직렬 처리.
//   - 메인은 결과 채널을 받아 DB INSERT 만 (직렬, 단일 커넥션).
//   - 본문이 빈 PDF 는 parse_status='empty' — 진짜 error 와 구분.
//
// DRM 운영 주의: 메인 프로세스는 보호 파일의 *내용* 을 절대 읽지 않는다.
// 메타데이터(File.Exists, 크기)만 — 실제 본문 read 는 격리된 워커 PdfPig 에서만.
// raw read 횟수가 DLP 임계에 걸려 메인이 silent 종료된 운영 회귀를 회피.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DocMine.Core.Config;
using DocMine.Core.Db;
using DocMine.Core.Pdf;
using MySqlConnector;

namespace DocMine.Core.Pipeline;

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
    private Action<string>? _onLog;

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
        _onLog = onLog;
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

        // ── 1) skip 대상 bulk 제외 — DB 기존 파일은 한 번에 카운트 ──
        // (skip 수만 건을 개별 Tick + onProgress 호출하면 UI 메시지 큐 폭주로 메인 GUI 죽음.)
        var stats = new Stats(rows.Count);
        var toProcess = new List<(int Idx, CsvRow Row)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (knownKeys.Contains(CsvIngestHelpers.NormKey(row.Directory, row.Filename)))
                continue;  // skip 대상 — 아래서 bulk 카운트
            toProcess.Add((start + i, row));
        }
        var skipCount = rows.Count - toProcess.Count;
        if (skipCount > 0)
        {
            stats.TickSkipBulk(skipCount);
            onProgress?.Invoke(stats.Snapshot());
        }
        onLog?.Invoke($"  처리 대상: {toProcess.Count:N0}건 (skip {skipCount:N0}건 제외)");

        // ── 처리 대상 → 작업 리스트 ──
        // 파일 존재 점검은 워커 단계로 미룬다(병렬 + 건별 진행). 메타데이터라 DRM 안전.
        var tasks = new List<(int Idx, string Path, CsvRow Row)>(toProcess.Count);
        foreach (var (idx, row) in toProcess)
            tasks.Add((idx, Path.Combine(row.Directory, row.Filename), row));

        var pendingCommit = 0;

        await using var conn = _repo.OpenConnection();
        await using var errLogStream = new StreamWriter(
            "pdf_parse_errors.csv", append: true,
            new System.Text.UTF8Encoding(true));

        // ── 2) 병렬 파싱 + 결과 채널 → 메인에서 INSERT ──
        if (tasks.Count == 0 || cancellationToken.IsCancellationRequested)
            goto Finalize;

        var workers = Math.Max(1, Math.Min(Environment.ProcessorCount, tasks.Count));
        onLog?.Invoke($"  파서 워커 {workers}개 병렬 (논리 CPU {Environment.ProcessorCount}개)…");

        // 작업을 워커 수로 라운드로빈 분배 — 각 워커 = 자기 큐를 직렬 처리.
        var queues = new List<List<(int Idx, string Path, CsvRow Row)>>();
        for (int i = 0; i < workers; i++) queues.Add(new List<(int, string, CsvRow)>());
        for (int i = 0; i < tasks.Count; i++) queues[i % workers].Add(tasks[i]);

        // 결과 채널 — 모든 워커가 push, 메인 INSERT 루프가 직렬 pull.
        // Bounded — 워커(병렬)가 직렬·단일커넥션 INSERT 보다 빨리 큰 본문 결과를 쌓으면
        // unbounded 큐가 메모리를 무한 점유 → OOM(메인 GUI silent 종료). 워커 수의 2배로
        // 제한해 큐가 차면 WriteAsync 가 대기(backpressure) — 워커가 INSERT 속도에 맞춰짐.
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

                    // 파일 존재 점검 (메타데이터 — 내용 read 아님, DRM 안전). 없으면 워커
                    // 호출 없이 error 결과만 채널로 — 진행바가 즉시 갱신됨.
                    if (!File.Exists(PdfTextExtractor.WinLongPath(t.Path)))
                    {
                        var miss = new WorkerResult(t.Row, "error", null, "파일 없음");
                        await channel.Writer.WriteAsync(miss, cancellationToken);
                        continue;
                    }

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

        // stderr 흡수만 — 워커당 ready 메시지가 다수라 LogPane 으로 흘리면 화면이
        // 어지럽고 UI thread 도 막힌다. buffer 가 차서 deadlock 되지 않게 끝까지 읽되 출력 안 함.
        _ = Task.Run(async () =>
        {
            try { await p.StandardError.ReadToEndAsync(); }
            catch { }
        });
        return p;
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
        catch (Exception ex)
        {
            // 진행은 막지 않되 UI 에 한 줄 남긴다 (예: max_allowed_packet 초과, 컬럼 길이 초과).
            _onLog?.Invoke($"  ⚠ INSERT 실패 — {row.Filename}: {ex.Message}");
        }
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

        /// <summary>DB 기존 파일 skip 을 한 번에 카운트 — 개별 Tick + onProgress 폭주 회피.</summary>
        public void TickSkipBulk(int n) { Cur += n; Skip += n; }

        public PdfInsertProgress Snapshot() => new(_total, Cur, Ok, Err, Skip, Empty);

        public string Summary()
        {
            var empty = Empty > 0 ? $" empty:{Empty}" : "";
            var skip  = Skip  > 0 ? $" skip:{Skip}"   : "";
            return $"  완료: ok:{Ok} err:{Err}{empty}{skip}";
        }
    }
}
