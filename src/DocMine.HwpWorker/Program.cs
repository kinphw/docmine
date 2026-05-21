// DocMine.HwpWorker — 한/글 COM 워커 (별도 .exe). Phase 4 에서 본격 구현.
// 현재는 빌드 통과용 stub.
//
// 향후 구조:
//   - STA + Activator.CreateInstance(Type.GetTypeFromProgID("HwpFrame.HwpObject"))
//   - stdin JSON-line: {"idx":N,"path":"...","ext":".hwp"}
//   - stdout JSON-line: {"idx":N,"status":"success","text":"...","err":null}
//   - Forge 의 ComLateBind / HwpSession 패턴 차용

namespace DocMine.HwpWorker;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Console.Error.WriteLine("DocMine.HwpWorker stub — Phase 4 에서 구현 예정");
        return 0;
    }
}
