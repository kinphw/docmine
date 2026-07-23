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
//   --keep-hwp               COM 재활용·종료 시 외부 Hwp.exe 죽이지 않음.
//                            인터랙티브 추출기(ExtractorTab) 에서만 사용 —
//                            사용자가 띄워둔 한/글 보호용.

using System.Text;
using System.Text.Json;
using DocMine.Core.Diff;
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
        var binReader = new HwpBinaryReader();
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
            if (req.Op == "parse2")
            {
                WriteResponse2(HandleParse2(req, zipReader, sectionParser, com));
                continue;
            }
            if (req.Op == "report")
            {
                WriteResponse(HandleReport(req, com));
                continue;
            }
            if (req.Op == "copyas")
            {
                WriteResponse(HandleCopyAs(req));
                continue;
            }
            if (req.Op != "parse")
            {
                WriteResponse(new ParseResponse(req.Idx, "error", null, $"알 수 없는 op: {req.Op}"));
                continue;
            }

            var resp = HandleParse(req, zipReader, binReader, sectionParser, com);
            WriteResponse(resp);
        }

        popupCts.Cancel();
        Console.Error.WriteLine("[HwpWorker] exit");
        return 0;
    }

    private static ParseResponse HandleParse(
        ParseRequest req,
        HwpxZipReader zipReader,
        HwpBinaryReader binReader,
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
                // 바이너리 .hwp — 1차: 매니지드 직접 파싱(COM/한글 무의존).
                //   배포용/암호화/구형/파싱실패, 또는 추출 0자 → COM 백엔드로 폴백(안전망).
                //   폴백이 걸리면 stderr 로 사유를 남겨 빈도 추적 가능하게 한다.
                try
                {
                    var doc = binReader.ReadDocument(req.Path);
                    text = doc.ExtractText(skipEmpty: true);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        Console.Error.WriteLine($"[HwpWorker] .hwp 매니지드 추출 0자 → COM 재확인: {Path.GetFileName(req.Path)}");
                        text = com.Extract(req.Path);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[HwpWorker] .hwp 매니지드→COM 폴백 ({ex.GetType().Name}): {ex.Message}");
                    text = com.Extract(req.Path);
                }
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

    // ── parse2: 구조화 추출 (문서 비교 v2) ────────────────────────────────
    //   .hwpx(비DRM) → ZIP 직접 파싱.
    //   .hwp(바이너리) / DRM .hwpx → COM 으로 HWPX 정규화(SaveAs) 후 ZIP 파싱.
    //   임시본이 DLP 에 재암호화돼 ZIP 파싱이 실패하면 error 로 반환 → 호출자가 평문 폴백.
    private static ParseResponse2 HandleParse2(
        ParseRequest req,
        HwpxZipReader zipReader,
        SectionParser sectionParser,
        HwpComExtractor com)
    {
        string? temp = null;
        try
        {
            var ext = (req.Ext ?? "").ToLowerInvariant();
            HwpxDocument doc;
            int pages = 0;

            if (ext == ".hwpx" && !HwpxZipReader.IsDrmProtected(req.Path))
            {
                doc = zipReader.ReadDocument(req.Path, sectionParser);
            }
            else
            {
                temp = com.SaveAsHwpx(req.Path, Path.GetTempPath());
                pages = com.LastPageCount;   // COM 으로 연 경우만 페이지 수 확보
                doc = zipReader.ReadDocument(temp, sectionParser);
            }

            var structure = DocStructure.FromHwpx(doc);
            structure.Pages = pages;
            return new ParseResponse2(req.Idx, "success", structure, null);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Length > 900) msg = msg[..900];
            return new ParseResponse2(req.Idx, "error", null, msg);
        }
        finally
        {
            if (temp is not null) { try { File.Delete(temp); } catch { } }
        }
    }

    // ── report: 비교 결과를 색상 입힌 HWP/HWPX 문서로 생성 ─────────────────
    private static ParseResponse HandleReport(ParseRequest req, HwpComExtractor com)
    {
        try
        {
            if (req.Report is null)
                return new ParseResponse(req.Idx, "error", null, "report 요청에 본문(runs)이 없습니다.");
            var format = string.IsNullOrWhiteSpace(req.Ext) ? "HWPX" : req.Ext!.ToUpperInvariant();
            com.WriteReport(req.Report, req.Path, format);
            return new ParseResponse(req.Idx, "success", null, null);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Length > 900) msg = msg[..900];
            return new ParseResponse(req.Idx, "error", null, msg);
        }
    }

    // ── copyas: 열린 상태(=DRM 인증 프로세스의 read = 복호화)로 바이트를 새 확장자로 저장 ──
    //   원본 확장자가 DRM 보호 대상이라 read 는 filter driver 가 복호화하고,
    //   대상 확장자(.hw1/.hw1x/.pd1)는 비보호라 write 시 재암호화되지 않는다(운영 DRM 가정).
    //   명시적 스트림 복사 — user-mode read 를 강제해 decrypt-on-read 경로를 탄다.
    //   내용 read 는 메인이 아닌 이 워커에서만(DLP silent-kill 회피 — DRM 불변식).
    private static ParseResponse HandleCopyAs(ParseRequest req)
    {
        try
        {
            var src = Path.GetFullPath(req.Path);
            if (string.IsNullOrWhiteSpace(req.Target))
                return new ParseResponse(req.Idx, "error", null, "대상 경로(target) 없음");
            var dst = Path.GetFullPath(req.Target);
            if (!File.Exists(src))
                return new ParseResponse(req.Idx, "error", null, "원본 파일 없음");
            if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                return new ParseResponse(req.Idx, "error", null, "원본과 대상 경로가 같음");

            using (var input  = new FileStream(src, FileMode.Open,   FileAccess.Read,  FileShare.Read))
            using (var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output, 1 << 20);

            // text 필드로 저장된 대상 경로를 돌려준다(로그·검증용).
            return new ParseResponse(req.Idx, "success", dst, null);
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

    private static void WriteResponse2(ParseResponse2 resp)
    {
        var json = JsonSerializer.Serialize(resp, JsonOpts);
        Console.Out.WriteLine(json);
        Console.Out.Flush();
    }

    private sealed record ParseRequest(string Op, int Idx, string Path, string? Ext, ReportDoc? Report = null, string? Target = null);
    private sealed record ParseResponse(int Idx, string Status, string? Text, string? Err);
    private sealed record ParseResponse2(int Idx, string Status, DocStructure? Doc, string? Err);
}
