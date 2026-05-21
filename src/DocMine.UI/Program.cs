// DocMine GUI 진입점. Python unified_gui.main() 등가.
// WinForms 표준 패턴 — STA + ApplicationConfiguration.Initialize() + MainForm.

using DocMine.Win32;

namespace DocMine.UI;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // 부모(이 프로세스) 가 어떻게 죽든 자식 워커(향후 HwpWorker) 도 함께
        // 종료되도록 Job Object 자가 할당. Python inserter.py _setup_kill_on_close_job 등가.
        // 실패해도 silent (디버거/샌드박스 등 — 보조 안전망일 뿐 critical 아님).
        JobObject.SetupKillOnClose();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
