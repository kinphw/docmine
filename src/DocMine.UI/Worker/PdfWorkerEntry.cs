// PdfWorker 모드 진입점 — Python multiprocessing.Pool(_extract_pdf_worker) 등가.
//
// 같은 python.exe 가 args 에 따라 세 모드로 분기:
//   - args 없음 / GUI args  → MainForm
//   - args == --hwp-worker  → HwpWorkerEntry.Run
//   - args == --pdf-worker  → PdfWorkerEntry.Run    (이 파일)
//
// 왜 별도 워커 프로세스인가:
//   PdfPig 가 매니지드 코드라도 큰/손상된 PDF 처리 시 OOM 또는 native-level
//   abort 가능 — Parallel.ForEachAsync 의 thread 격리는 같은 프로세스라 한
//   thread 가 죽으면 메인 GUI 까지 silent 종료. Python mp.Pool 처럼 프로세스
//   격리해야 한 워커가 죽어도 메인 + 다른 워커 살아남고 새 워커 spawn 으로 회복.
//
// STDIO 프로토콜 (line-delimited JSON) — HwpWorker 와 동일 스타일:
//   IN  : {"op":"parse","idx":N,"path":"..."}
//         {"op":"quit"}
//   OUT : {"idx":N,"status":"success|empty|error","text":"...","err":null}
//
// 한 워커 = 한 작업씩 직렬. 병렬은 메인이 워커 N개 spawn 으로 처리.

using System.Text;
using System.Text.Json;
using DocMine.Core.Pdf;

namespace DocMine.UI.Worker;

internal static class PdfWorkerEntry
{
    private const string EmptyTextMsg = "본문 텍스트 없음 (스캔본/이미지 PDF — OCR 미적용)";

    // 본문 안전 상한 (문자 수) — *정상* 문서를 자르려는 게 아니다. 손상/악성 PDF 가
    // 추출 단계에서 폭주해 수백 MB~GB 텍스트를 토해내는 경우만 막는 가드.
    // 3천만 자(≈1만 쪽)는 어떤 정상 문서보다도 크므로 실문서는 절대 절단되지 않는다.
    // DB max_allowed_packet 기준 절단은 메인(PdfInsertRunner)이 별도로 처리하고,
    // 자를 땐 로그로 명시한다.
    private const int MaxBodyChars = 30_000_000;

    public static int Run(string[] args)
    {
        // WinExe 라 콘솔 없음 — pipe 위에 UTF-8 wrapper 직접 부착 (HwpWorker 와 동일).
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));

        Console.Error.WriteLine($"[PdfWorker] ready (PID={Environment.ProcessId})");

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
                WriteResponse(new ParseResponse(-1, "error", null, $"잘못된 요청: {ex.Message}", 0));
                continue;
            }

            if (req.Op == "quit")
            {
                Console.Error.WriteLine("[PdfWorker] quit received");
                break;
            }
            if (req.Op != "parse")
            {
                WriteResponse(new ParseResponse(req.Idx, "error", null, $"알 수 없는 op: {req.Op}", 0));
                continue;
            }

            ParseResponse resp;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var text = PdfTextExtractor.Extract(req.Path);
                sw.Stop();
                if (string.IsNullOrEmpty(text))
                {
                    resp = new ParseResponse(req.Idx, "empty", null, EmptyTextMsg, sw.ElapsedMilliseconds);
                }
                else
                {
                    if (text.Length > MaxBodyChars)
                    {
                        Console.Error.WriteLine($"[PdfWorker] 비정상적으로 긴 추출 {text.Length:N0}자 — 손상 PDF 의심, {MaxBodyChars:N0}자로 절단");
                        text = text[..MaxBodyChars] + "\n…[비정상적으로 긴 추출 — 손상 PDF 의심, 상한 절단]";
                    }
                    resp = new ParseResponse(req.Idx, "success", text, null, sw.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                var msg = ex.Message;
                if (msg.Length > 900) msg = msg[..900];
                resp = new ParseResponse(req.Idx, "error", null, msg, sw.ElapsedMilliseconds);
            }
            WriteResponse(resp);
        }

        Console.Error.WriteLine("[PdfWorker] exit");
        return 0;
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

    private sealed record ParseRequest(string Op, int Idx, string Path);
    private sealed record ParseResponse(int Idx, string Status, string? Text, string? Err, long ElapsedMs);
}
