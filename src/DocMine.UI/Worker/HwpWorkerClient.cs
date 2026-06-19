// HwpWorkerClient — 인터랙티브한 HWP/HWPX 텍스트 추출용 워커 클라이언트.
//
// ExtractorTab 의 StartHwpWorker + ExtractHwp 로직을 재사용 가능한 형태로 정리.
// CompareTab(문서 비교) 가 사용. 같은 binary 를 --hwp-worker 모드로 spawn 하고
// STDIO JSON 으로 파일별 파싱을 요청한다.
//
// ★ 모든 파일 *내용 읽기* 는 이 워커(자식 프로세스) 안에서만 일어난다.
//   DRM/DLP 솔루션이 보호 파일 접근 시 프로세스를 강제 종료해도 메인 GUI 는
//   살아남아야 하기 때문 (HWPX 도 ZIP 직접 읽기를 워커에서 수행).
//
// --keep-hwp 로 띄워, 사용자가 따로 열어둔 한/글이 추출 중 함께 종료되지 않게 한다.
//
// 동기 ReadLine 블로킹이라 호출자는 백그라운드 스레드(Task.Run) 에서 Extract 할 것.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DocMine.Core.Diff;

namespace DocMine.UI.Worker;

internal sealed class HwpWorkerClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Process _proc;
    private readonly Task _stderrTask;
    private int _idx;

    public HwpWorkerClient(Action<string>? onStderr = null)
    {
        // 자기 자신(개발 docmine.exe / 배포 python.exe) 을 워커 모드로 재실행.
        var workerPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath null — 워커 spawn 불가");

        var psi = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--hwp-worker");
        psi.ArgumentList.Add("--keep-hwp");   // 사용자가 띄워둔 한/글 보호

        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("HwpWorker spawn 실패");

        var p = _proc;
        _stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await p.StandardError.ReadLineAsync()) is not null)
                    if (!string.IsNullOrWhiteSpace(line)) onStderr?.Invoke(line);
            }
            catch { /* 워커 종료 시 자연 EOF */ }
        });
    }

    public int ProcessId => _proc.Id;

    /// <summary>파일 1건의 본문 텍스트 추출. status=error 면 예외. (블로킹)</summary>
    public string Extract(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var req = new
        {
            op   = "parse",
            idx  = _idx++,
            path = Path.GetFullPath(path),
            ext  = Path.GetExtension(path).ToLowerInvariant(),
        };
        _proc.StandardInput.WriteLine(JsonSerializer.Serialize(req, JsonOpts));
        _proc.StandardInput.Flush();

        var line = _proc.StandardOutput.ReadLine()
            ?? throw new IOException("워커 stdout EOF — 프로세스가 종료된 듯합니다 (DRM/DLP 강제 종료 가능성).");

        var resp = JsonSerializer.Deserialize<Resp>(line, JsonOpts)
            ?? throw new IOException("워커 응답 파싱 실패");

        if (resp.Status == "error")
            throw new InvalidOperationException(resp.Err ?? "(알 수 없는 오류)");

        return resp.Text ?? "";
    }

    /// <summary>
    /// 파일 1건의 구조화 추출(문단/표). 비DRM .hwpx 는 워커에서 ZIP 직접,
    /// 바이너리 .hwp / DRM 은 COM SaveAs→HWPX 정규화 후 파싱. 실패 시 예외(호출자 폴백). (블로킹)
    /// </summary>
    public DocStructure ExtractStructure(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var req = new
        {
            op   = "parse2",
            idx  = _idx++,
            path = Path.GetFullPath(path),
            ext  = Path.GetExtension(path).ToLowerInvariant(),
        };
        _proc.StandardInput.WriteLine(JsonSerializer.Serialize(req, JsonOpts));
        _proc.StandardInput.Flush();

        var line = _proc.StandardOutput.ReadLine()
            ?? throw new IOException("워커 stdout EOF — 프로세스가 종료된 듯합니다 (DRM/DLP 강제 종료 가능성).");

        var resp = JsonSerializer.Deserialize<StructResp>(line, JsonOpts)
            ?? throw new IOException("워커 응답 파싱 실패");

        if (resp.Status == "error")
            throw new InvalidOperationException(resp.Err ?? "(알 수 없는 오류)");

        return resp.Doc ?? new DocStructure();
    }

    /// <summary>
    /// 비교 결과(ReportDoc)를 색상 입힌 HWP/HWPX 문서로 생성·저장 (한/글 COM 필요). (블로킹)
    /// </summary>
    /// <param name="format">"HWP" 또는 "HWPX".</param>
    public void GenerateReport(ReportDoc doc, string outPath, string format, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var req = new
        {
            op     = "report",
            idx    = _idx++,
            path   = Path.GetFullPath(outPath),
            ext    = format,
            report = doc,
        };
        _proc.StandardInput.WriteLine(JsonSerializer.Serialize(req, JsonOpts));
        _proc.StandardInput.Flush();

        var line = _proc.StandardOutput.ReadLine()
            ?? throw new IOException("워커 stdout EOF — 프로세스가 종료된 듯합니다.");

        var resp = JsonSerializer.Deserialize<Resp>(line, JsonOpts)
            ?? throw new IOException("워커 응답 파싱 실패");

        if (resp.Status == "error")
            throw new InvalidOperationException(resp.Err ?? "(알 수 없는 오류)");
    }

    public void Dispose()
    {
        try { _proc.StandardInput.WriteLine("{\"op\":\"quit\"}"); _proc.StandardInput.Flush(); } catch { }
        try { _proc.WaitForExit(3000); } catch { }
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        try { _proc.Dispose(); } catch { }
    }

    private sealed record Resp(int Idx, string Status, string? Text, string? Err);
    private sealed record StructResp(int Idx, string Status, DocStructure? Doc, string? Err);
}
