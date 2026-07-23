// 헤드리스 적재 테스트 진입점 — docmine.exe --insert-test <scanDir> [hwpW] [pdfW] [dbName]
//
// 워커 spawn 이 Environment.ProcessPath(자기 자신 재실행)라, 적재 러너는 반드시
// docmine.exe 안에서 실행돼야 --hwp-worker/--pdf-worker 자식이 제대로 뜬다.
// GUI(InsertTab) 없이 멀티워커 병렬 적재를 실측하기 위한 개발/검증 전용 모드.
//
// DB: 현재 설정의 host/port 를 재사용하고 계정(root)·DB 이름만 오버라이드 → 실데이터 무침범.

using System.Diagnostics;
using System.Text;
using DocMine.Core.Config;
using DocMine.Core.Pipeline;
using DocMine.Core.Scanning;

namespace DocMine.UI.Worker;

internal static class InsertTestEntry
{
    public static int Run(string[] args)
    {
        var utf8 = new UTF8Encoding(false);
        try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true }); } catch { }

        if (args.Length < 2)
        {
            Console.WriteLine("usage: --insert-test <scanDir> [hwpWorkers] [pdfWorkers] [dbName]");
            return 1;
        }
        var scanDir = args[1];
        int? hwpW = args.Length > 2 && int.TryParse(args[2], out var a) ? a : null;
        int? pdfW = args.Length > 3 && int.TryParse(args[3], out var b) ? b : null;
        var dbName = args.Length > 4 ? args[4] : "docmine_test";

        // ── 1) 스캔 → CSV ──
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".hwp", ".hwpx", ".pdf" };
        var files = new List<ScannedFile>();
        foreach (var f in Directory.EnumerateFiles(scanDir, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (!exts.Contains(ext)) continue;
            try
            {
                var fi = new FileInfo(f);
                files.Add(new ScannedFile(fi.DirectoryName ?? "", fi.Name, ext, fi.Length,
                    fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));
            }
            catch { }
        }
        int nh = files.Count(f => f.Extension is ".hwp" or ".hwpx");
        int np = files.Count(f => f.Extension == ".pdf");
        Console.WriteLine($"[스캔] {scanDir} → HWP {nh} · PDF {np} (총 {files.Count})");
        if (files.Count == 0) return 0;

        var csv = Path.Combine(Path.GetTempPath(), $"docmine_insert_test_{Guid.NewGuid():N}.csd");
        DriveScanner.WriteCsv(files, csv);

        // ── 2) 테스트 AppConfig (host/port 재사용, 계정·DB 만 오버라이드) ──
        var cfg = AppConfig.Current with { DbUser = "root", DbPassword = "genius", DbName = dbName, DbTable = "documents" };
        Console.WriteLine($"[DB] {cfg.DbUser}@{cfg.DbHost}:{cfg.DbPort}/{cfg.DbName} (table={cfg.DbTable})");

        // ── 3) 배분 ── 순차 실행이라 각 단계가 논리 CPU 전량 사용(maxWorkers 미지정 시).
        //   hwpW/pdfW 인자는 단독 측정(0=건너뜀)·강제 워커수 지정용으로만 유지.
        Console.WriteLine($"[배분] 순차 실행 · 각 단계 워커: HWP {hwpW?.ToString() ?? "전량"} · PDF {pdfW?.ToString() ?? "전량"}");

        // ── 4) 순차 실행 (InsertTab 과 동일 구조) — HWP → PDF ──
        var sw = Stopwatch.StartNew();
        int hwpLast = 0, pdfLast = 0;
        try
        {
            if (nh > 0 && hwpW != 0)   // hwpW=0 → HWP 건너뜀(단독 PDF 측정용)
                new HwpInsertRunner(cfg).RunAsync(csv, 0, null,
                    onLog: l => Console.WriteLine($"  [HWP] {l}"),
                    onProgress: p => { if (p.Index == p.Total || p.Index - hwpLast >= Math.Max(1, p.Total / 20)) { hwpLast = p.Index; Console.WriteLine($"  [HWP] … {p.Index}/{p.Total} ok:{p.Ok} err:{p.Err} crash:{p.Crash}"); } },
                    cancellationToken: CancellationToken.None, maxWorkers: hwpW).GetAwaiter().GetResult();
            if (np > 0 && pdfW != 0)   // pdfW=0 → PDF 건너뜀(단독 HWP 측정용)
                new PdfInsertRunner(cfg).RunAsync(csv, 0, null,
                    onLog: l => Console.WriteLine($"  [PDF] {l}"),
                    onProgress: p => { if (p.Index == p.Total || p.Index - pdfLast >= Math.Max(1, p.Total / 20)) { pdfLast = p.Index; Console.WriteLine($"  [PDF] … {p.Index}/{p.Total} ok:{p.Ok} err:{p.Err} empty:{p.Empty}"); } },
                    cancellationToken: CancellationToken.None, maxWorkers: pdfW).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Console.WriteLine($"[오류] {ex.GetType().Name}: {ex.Message}"); }
        sw.Stop();

        var secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        Console.WriteLine($"[완료] 벽시계 {secs:F1}s · {files.Count}건 · {files.Count / secs:F1} 건/s");
        try { File.Delete(csv); } catch { }
        return 0;
    }
}
