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

    // 본문 INSERT 시 허용 byte 예산 — 서버 max_allowed_packet 에서 산출 (RunAsync 에서 설정).
    // 이걸 넘으면 INSERT 자체가 실패하므로, 넘는 문서는 '잘라서라도' 색인 (전무보다 부분이 나음).
    private long _bodyByteBudget = 16L * 1024 * 1024;
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

        // ── 1) skip 대상 bulk 제외 — DB 기존 파일은 한 번에 카운트 ──
        // (예전엔 skip 수만 건을 루프 돌며 stats.Tick + onProgress 호출 → UI 메시지
        //  큐 폭주로 메인 GUI 가 죽음. skip 은 통계만 필요하니 bulk 처리.)
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
        // 파일 존재 점검은 워커 단계로 미룬다(병렬 + 건별 진행). 예전엔 여기서 전 파일을
        // 직렬 File.Exists 점검했는데, skip 이 적을 때(예: 빈 테이블) 수만 건을 로그·진행
        // 없이 훑어 '멈춘 것처럼' 보였다. 점검 자체는 메타데이터라 DRM 에도 안전.
        var tasks = new List<(int Idx, string Path, CsvRow Row)>(toProcess.Count);
        foreach (var (idx, row) in toProcess)
            tasks.Add((idx, Path.Combine(row.Directory, row.Filename), row));

        var pendingCommit = 0;

        await using var conn = _repo.OpenConnection();
        _bodyByteBudget = QueryPacketBudget(conn);
        await using var errLogStream = new StreamWriter(
            "pdf_parse_errors.csv", append: true,
            new System.Text.UTF8Encoding(true));

        // 성능 비교용 벽시계 — 파싱 + INSERT 전체 구간.
        var wall = System.Diagnostics.Stopwatch.StartNew();

        // ── 2) 병렬 파싱 + 결과 채널 → 메인에서 INSERT ──
        if (tasks.Count == 0 || cancellationToken.IsCancellationRequested)
            goto Finalize;

        var workers = Math.Max(1, Math.Min(AppConfig.PdfWorkers, tasks.Count));
        var cpu = Environment.ProcessorCount;
        onLog?.Invoke($"  PDF 엔진: {_cfg.PdfEngine}  |  파서 워커 {workers}개 병렬 (논리 CPU {cpu}개)…");

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
                // INSERT 직전/직후를 추적 — silent crash 시 마지막 줄이 'INS >>>' 면
                // 그 본문 길이(len)에서 INSERT 중 죽은 것 (거대 본문 OOM/패킷 초과 분류용).
                CrashTrace($"INS >>> {r.Row.Filename}  status={r.Status} len={(r.Text?.Length ?? 0):N0}");
                await InsertAsync(conn, r.Row, r.Text, r.Status, r.ErrMsg);
                CrashTrace($"INS <<< {r.Row.Filename}  ok");
                pendingCommit++;
                stats.Tick(r.Status);
                stats.AddParse(r.ElapsedMs);
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
                        CrashTrace($"[{t.Idx}] >>> {t.Path}  (파일 없음)");
                        var miss = new WorkerResult(t.Row, "error", null, "파일 없음", 0);
                        CrashTrace($"[{t.Idx}] <<< error(missing)");
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

                    CrashTrace($"[{t.Idx}] >>> {t.Path}  {SniffSize(t.Row.SizeBytes)}");
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
                        result = new WorkerResult(t.Row, resp.Status, resp.Text, resp.Err, resp.ElapsedMs);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // 워커 crash 또는 통신 오류 — 이 작업은 error 처리, 다음 작업 위해 새 워커 spawn.
                        var msg = $"워커 crash: {ex.Message}";
                        if (msg.Length > 900) msg = msg[..900];
                        result = new WorkerResult(t.Row, "error", null, msg, 0);
                        try { if (worker is not null && !worker.HasExited) worker.Kill(entireProcessTree: true); } catch { }
                        try { worker?.Dispose(); } catch { }
                        worker = null;
                    }
                    CrashTrace($"[{t.Idx}] <<< {result.Status} {result.ElapsedMs}ms");
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

        // ── 성능 요약 (엔진 A/B 비교용) ──
        wall.Stop();
        var secs = wall.Elapsed.TotalSeconds;
        var avgMs = stats.ParsedCount > 0 ? stats.TotalParseMs / (double)stats.ParsedCount : 0;
        var thru = secs > 0 ? stats.ParsedCount / secs : 0;
        onLog?.Invoke(
            $"  [성능] 엔진={_cfg.PdfEngine}  벽시계={secs:F1}s  " +
            $"파싱평균={avgMs:F0}ms/건  처리량={thru:F1}건/s  " +
            $"(파싱 {stats.ParsedCount:N0}건, 파싱시간합 {stats.TotalParseMs:N0}ms)");

        onLog?.Invoke("  에러 로그: pdf_parse_errors.csv");
        onLog?.Invoke("  (스캔본/이미지 PDF 는 'empty' 로 분류 — 에러 아님)");
        return 0;
    }

    private record WorkerResult(CsvRow Row, string Status, string? Text, string? ErrMsg, long ElapsedMs);
    private sealed record WorkerResponse(int Idx, string Status, string? Text, string? Err, long ElapsedMs);

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
        psi.ArgumentList.Add(_cfg.PdfEngine);   // 워커가 쓸 엔진 — run 중 일관성 보장

        var p = Process.Start(psi) ?? throw new InvalidOperationException("PdfWorker spawn 실패");

        // stderr 흡수만 — 워커당 ready/repair 메시지가 다수라 LogPane 으로 흘리면 화면이
        // 어지럽고 UI thread 도 막힌다. buffer 가 차서 deadlock 되지 않게 끝까지 읽되 출력 안 함.
        _ = Task.Run(async () =>
        {
            try { await p.StandardError.ReadToEndAsync(); }
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
            cmd.Parameters.AddWithValue("@body", (object?)FitBody(text, row.Filename) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@err", (object?)errMsg ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // 진행은 막지 않되, 왜 INSERT 가 조용히 실패했는지는 남긴다
            // (예: max_allowed_packet 초과, 교착, 컬럼 길이 초과).
            CrashTrace($"INS FAIL {row.Filename}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>서버 max_allowed_packet 조회 → 본문 byte 예산 산출 (다른 컬럼+프로토콜 여유 차감).
    /// 결과는 FitBody 의 안전망(거대 본문만 절단)에 쓰이며, 실제 절단 시에만 로그를 남긴다.</summary>
    private static long QueryPacketBudget(MySqlConnection conn)
    {
        long maxPacket = 16L * 1024 * 1024;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT @@max_allowed_packet";
            maxPacket = Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch { /* 조회 실패 시 보수적 16MB 가정 */ }

        return Math.Max(256 * 1024, maxPacket - 512 * 1024);  // 512KB 여유
    }

    /// <summary>
    /// 본문을 max_allowed_packet 예산 이내로 맞춤. 예산 안이면 원문 그대로 — 정상/긴 문서는
    /// 절대 손대지 않는다. 초과 시에만 잘라 '부분 색인'하고 로그로 명시 (INSERT 통째 실패 회피).
    /// </summary>
    private string? FitBody(string? text, string filename)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (System.Text.Encoding.UTF8.GetByteCount(text) <= _bodyByteBudget) return text;  // 대부분 여기

        // UTF-8 최악 4 byte/문자 가정으로 자르면 반드시 예산 이내.
        var keep = (int)Math.Min(text.Length, _bodyByteBudget / 4);
        var cut = text[..keep] + "\n…[DB max_allowed_packet 한계로 본문 일부만 색인됨]";
        _onLog?.Invoke($"  ⚠ 본문 절단(패킷 한계): {filename} — {text.Length:N0}자 중 {keep:N0}자만 색인 (서버 max_allowed_packet 상향 시 전체 색인 가능)");
        CrashTrace($"BODY TRUNCATED {filename}: {text.Length:N0} chars > budget {_bodyByteBudget:N0}B");
        return cut;
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

    // ─ 트레이스용 크기 표기 — 파일 시스템 접근 0 ────────────────────
    //
    // 과거엔 메인이 파싱 전 SniffPdf 로 파일 첫/끝 16KB 를 raw byte 로 읽어
    // producer/필터를 분류했다. 그러나 DRM(Fasoo 등) 환경에서 **메인 프로세스가
    // 보호 파일의 raw 바이트를 읽고 tail 로 seek 하는 행위**가 DLP/추출방지의
    // 횟수 임계에 걸려 N 번째 파일에서 프로세스가 강제 종료(GUI silent 증발)되는
    // 것으로 강하게 의심된다. (전체 CSV 는 게이지 N 에서 죽고, 그 부분집합만 돌리면
    // 정상인 패턴 = 파일 *내용*이 아니라 raw read *횟수*가 변수라는 뜻.)
    // Python 코디네이터엔 이런 raw read 가 없었고 crash 도 없었다.
    //   → 메인은 파일을 일절 열지 않는다. 크기는 스캔 CSV 가 이미 수집한 값을 사용.
    //     (실제 본문 read 는 격리된 워커 프로세스의 iText 에서만 발생.)
    private static string SniffSize(long sizeBytes)
        => sizeBytes >= 1024 * 1024
            ? $"size={sizeBytes / 1024.0 / 1024.0:F1}MB"
            : $"size={sizeBytes / 1024.0:F0}KB";

    // ─ 통계 ──────────────────────────────────────────────────────────
    private sealed class Stats
    {
        public int Cur;
        public int Ok, Err, Skip, Empty;
        public long TotalParseMs;   // 워커 파싱 시간 합 (성능 비교)
        public int ParsedCount;     // 파싱 시간이 집계된 건수
        private readonly int _total;
        public Stats(int total) => _total = total;

        /// <summary>워커가 보고한 파싱 소요(ms) 누적 — 단일 소비자(insertTask)에서만 호출.</summary>
        public void AddParse(long ms) { if (ms > 0) { TotalParseMs += ms; ParsedCount++; } }

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
