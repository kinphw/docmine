// DocMine 단일 진입점 — args 에 따라 GUI 모드 / 워커 모드 분기.
//
// Python 의 'pythonw.exe' + 'mp.Process(worker_main)' 패턴을 한 binary 로 통합:
//   - 산출 파일은 python.exe 하나
//   - args 없음 / GUI args → MainForm
//   - args == --hwp-worker → HwpWorkerEntry.Run (콘솔 STDIO 루프)
//
// HwpInsertRunner / ExtractorTab 이 워커를 spawn 할 때는
// Process.Start(Environment.ProcessPath, "--hwp-worker") — 같은 binary 재실행.

using DocMine.UI.Worker;
using DocMine.Win32;

namespace DocMine.UI;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // ── 워커 모드 ─────────────────────────────────────────────────
        // 부모(메인 GUI) 가 RedirectStandardInput/Output=true 로 띄움 →
        // 자식 WinExe 의 Console.In/Out 이 부모 pipe 로 자동 연결됨.
        if (args.Length > 0 && args[0] == "--hwp-worker")
            return HwpWorkerEntry.Run(args);
        if (args.Length > 0 && args[0] == "--pdf-worker")
            return PdfWorkerEntry.Run(args);
        // 진단용 헤드리스 적재 — GUI 없이 PdfInsertRunner 전체 경로 재현.
        if (args.Length > 0 && args[0] == "--pdf-insert-test")
            return PdfInsertTestEntry.Run(args);

        // ── GUI 모드 ──────────────────────────────────────────────────
        // 부모가 어떻게 죽든 자식 워커 인스턴스도 함께 종료되도록 Job Object 자가 할당.
        // 실패해도 silent (디버거/샌드박스 등 — 보조 안전망일 뿐 critical 아님).
        JobObject.SetupKillOnClose();

        // Parallel.ForEachAsync 내부 throw 가 catch 빠져나가 silent 종료되지 않도록.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            MessageBox.Show(
                $"{e.Exception.GetType().Name}\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "DocMine — UI 스레드 예외", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            MessageBox.Show(
                $"{ex?.GetType().Name ?? "Unknown"}\n\n{ex?.Message}\n\n{ex?.StackTrace}",
                "DocMine — 처리되지 않은 예외", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            MessageBox.Show(
                $"{e.Exception.GetType().Name}\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "DocMine — 처리되지 않은 Task 예외", MessageBoxButtons.OK, MessageBoxIcon.Error);
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
