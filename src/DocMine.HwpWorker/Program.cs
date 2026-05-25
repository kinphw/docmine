// DocMine.HwpWorker.exe — 한/글 COM 워커 (별도 프로세스).
//
// Python multiprocessing.Process(worker_main) 의 등가물.
// .NET 의 Parallel.ForEachAsync 는 thread 공유라 한/글 COM 의 thread-affinity 와
// 비호환 → 별도 프로세스 격리 필수.
//
// STDIO 프로토콜 (line-delimited JSON):
//   IN  (stdin):  {"op":"parse","idx":N,"path":"...","ext":".hwp"}
//                 {"op":"quit"}
//   OUT (stdout): {"idx":N,"status":"success|error|empty|skip","text":"...","err":null}
//
// 메인 프로세스(HwpInsertRunner) 는 한 번에 한 줄 보내고 응답 받음 — 직렬 통신.
//
// CLI 옵션:
//   --keep-hwp   COM 재활용 시 외부 Hwp.exe 죽이지 않음 (추출기에서 사용자가 띄운 한/글 보호용)

using System.Text.Json;
using DocMine.Core.Hwp;
using DocMine.HwpWorker;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding  = System.Text.Encoding.UTF8;

        var killOnRestart = !args.Contains("--keep-hwp");

        using var popupCts = new CancellationTokenSource();
        PopupDismisser.Start(popupCts.Token);

        using var com = new HwpComExtractor(restartEvery: 500, killOnRestart: killOnRestart);

        var zipReader = new HwpxZipReader();
        var sectionParser = new SectionParser();

        Console.Error.WriteLine($"[HwpWorker] ready (PID={Environment.ProcessId}, keepHwp={!killOnRestart})");

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
}

internal sealed record ParseRequest(string Op, int Idx, string Path, string? Ext);
internal sealed record ParseResponse(int Idx, string Status, string? Text, string? Err);
