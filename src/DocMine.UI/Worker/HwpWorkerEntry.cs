// HwpWorker 모드 진입점 — Python multiprocessing.Process(worker_main) 의 등가.
//
// 같은 binary(python.exe) 가 args 에 따라 두 모드로 분기:
//   - args 없음 / GUI args  → Program.Main → Application.Run(MainForm)
//   - args == --hwp-worker  → HwpWorkerEntry.Run
//
// Python 의 `mp.Process(target=worker_main)` 가 같은 인터프리터 바이너리를
// spawn 하는 패턴과 1:1.  보조 binary 없음 — 산출 파일 한 개로 모든 역할 분담.
//
// STDIO 프로토콜 (line-delimited JSON):
//   IN  (stdin):  {"op":"parse","idx":N,"path":"...","ext":".hwp"}
//                 {"op":"quit"}
//   OUT (stdout): {"idx":N,"status":"success|error|empty|skip","text":"...","err":null}
//
// WinExe + STDIO 동작:
//   부모(메인 GUI) 가 RedirectStandardInput/Output=true 로 Process.Start 하면
//   자식 WinExe 의 Console.In/Out 이 부모 pipe 로 자동 연결. AllocConsole 불필요.
//   사용자가 cmd 에서 직접 'python.exe --hwp-worker' 치면 콘솔 안 떠서 stdin
//   입력 불가 — 디버깅용 케이스라 launch.json 의 'HwpWorker (stdin 수동)'
//   구성이 integratedTerminal 로 콘솔 제공.
//
// CLI 옵션:
//   --hwp-worker             (필수) 워커 모드 진입
//   --keep-hwp               COM 재활용 시 외부 Hwp.exe 죽이지 않음
//                            (추출기에서 사용자가 띄운 한/글 보호)

using System.Text;
using System.Text.Json;
using DocMine.Core.Hwp;

namespace DocMine.UI.Worker;

internal static class HwpWorkerEntry
{
    public static int Run(string[] args)
    {
        // WinExe 는 콘솔이 없어 Console.OutputEncoding setter 가 SetConsoleOutputCP 호출 시
        // invalid handle 로 throw. 부모가 RedirectStandardOutput=true 로 띄워 pipe 는 살아
        // 있으므로 SetOut/SetIn/SetError 로 UTF-8 wrapper 만 직접 갈아끼움.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));

        var keepHwp = args.Contains("--keep-hwp");

        using var popupCts = new CancellationTokenSource();
        PopupDismisser.Start(popupCts.Token);

        using var com = new HwpComExtractor(restartEvery: 500, killOnRestart: !keepHwp);

        var zipReader = new HwpxZipReader();
        var sectionParser = new SectionParser();

        Console.Error.WriteLine($"[HwpWorker] ready (PID={Environment.ProcessId}, keepHwp={keepHwp})");

        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            ParseRequest req;
            try
            {
                req = JsonSerializer.Deserialize<ParseRequest>(line, JsonOpts)!;
            }
            catch (Exception ex)
            {
                WriteResponse(new ParseResponse(-1, "error", null, $"잘못된 요청: {ex.Message}"));
                continue;
            }

            if (req.Op == "quit")
            {
                Console.Error.WriteLine("[HwpWorker] quit received");
                break;
            }
            if (req.Op != "parse")
            {
                WriteResponse(new ParseResponse(req.Idx, "error", null, $"알 수 없는 op: {req.Op}"));
                continue;
            }

            var resp = HandleParse(req, zipReader, sectionParser, com);
            WriteResponse(resp);
        }

        popupCts.Cancel();
        Console.Error.WriteLine("[HwpWorker] exit");
        return 0;
    }

    private static ParseResponse HandleParse(
        ParseRequest req,
        HwpxZipReader zipReader,
        SectionParser sectionParser,
        HwpComExtractor com)
    {
        try
        {
            var ext = (req.Ext ?? "").ToLowerInvariant();
            string text;

            if (ext == ".hwpx")
            {
                // 1차: ZIP 직접. DRM 이면 COM fallback.
                try
                {
                    var doc = zipReader.ReadDocument(req.Path, sectionParser);
                    text = doc.ExtractText(skipEmpty: true);
                }
                catch (HwpxDrmError)
                {
                    text = com.Extract(req.Path);
                }
            }
            else
            {
                text = com.Extract(req.Path);
            }

            return new ParseResponse(req.Idx, "success", text, null);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Length > 900) msg = msg[..900];
            return new ParseResponse(req.Idx, "error", null, msg);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void WriteResponse(ParseResponse resp)
    {
        var json = JsonSerializer.Serialize(resp, JsonOpts);
        Console.Out.WriteLine(json);
        Console.Out.Flush();
    }

    private sealed record ParseRequest(string Op, int Idx, string Path, string? Ext);
    private sealed record ParseResponse(int Idx, string Status, string? Text, string? Err);
}
