// 진단용 헤드리스 진입점 — GUI 없이 PdfInsertRunner 전체 파이프라인을 그대로 실행.
//
//   python.exe --pdf-insert-test <csvPath> [table]
//
// 목적: 운영 환경 silent crash 를 개발/테스트 환경에서 동일 코드로 재현.
// GUI 의 PdfInsertTab 과 같은 RunAsync 경로(SniffPdf·채널·워커 spawn·INSERT)를 타되,
// 로그는 stdout + pdf_insert_test.log 로 남긴다. (WinExe 라 stdout 리다이렉트 시 캡처됨)

using System.Text;
using DocMine.Core.Config;
using DocMine.Core.Pipeline;

namespace DocMine.UI.Worker;

internal static class PdfInsertTestEntry
{
    public static int Run(string[] args)
    {
        var utf8 = new UTF8Encoding(false);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });

        if (args.Length < 2)
        {
            Console.WriteLine("usage: python.exe --pdf-insert-test <csvPath> [table] [engine]");
            return 2;
        }
        var csv = args[1];
        var table = args.Length > 2 ? args[2] : null;
        var engine = args.Length > 3 ? args[3] : null;   // "iText" | "PdfPig" — A/B 비교 override

        using var logFile = new StreamWriter("pdf_insert_test.log", append: false, utf8) { AutoFlush = true };
        void Log(string s) { Console.WriteLine(s); logFile.WriteLine(s); }

        var cfg = AppConfig.Current;
        if (table is not null) cfg = cfg with { DbTable = table };
        if (engine is not null) cfg = cfg with { PdfEngine = engine };

        Log($"[test] csv={csv}");
        Log($"[test] DB={cfg.DbUser}@{cfg.DbHost}:{cfg.DbPort}/{cfg.DbName} table={cfg.DbTable}");
        Log($"[test] engine={cfg.PdfEngine}");
        Log($"[test] ProcessPath={Environment.ProcessPath}");

        try
        {
            var runner = new PdfInsertRunner(cfg);
            runner.RunAsync(
                csv, 0, null,
                onLog: Log,
                onProgress: p => Log($"  progress {p.Index}/{p.Total} ok:{p.Ok} err:{p.Err} empty:{p.Empty} skip:{p.Skip}"),
                cancellationToken: default).GetAwaiter().GetResult();
            Log("[test] 완료 — 정상 종료");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"[test] 예외: {ex.GetType().FullName}: {ex.Message}");
            Log(ex.StackTrace ?? "");
            return 1;
        }
    }
}
