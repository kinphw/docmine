// 한/글 COM 호출 도중 떠오르는 팝업("접근 허용", "확인" 등) 자동 클릭.
// Python inserter.py:_popup_loop 1:1 포팅.
//
// 동작: 300ms 주기로 모든 top-level window 순회 → 자식 Button 검사 →
//      라벨이 BTNS 리스트와 일치하면 BM_CLICK 전송.
//
// 안전성: COM 호출 스레드와 별개로 떠 있어도 영향 없음 (UI 메시지 전송만).
//        HwpWorker.exe 가 종료되면 ct 가 cancel 되어 루프 빠져나옴.

using System.Runtime.InteropServices;
using System.Text;

namespace DocMine.HwpWorker;

internal static class PopupDismisser
{
    private const uint BM_CLICK = 0xF5;

    // 한/글 보안 팝업의 한국어 + 영문 버튼 라벨.
    // & 표기는 alt-shortcut — 실제 텍스트에 포함됨 (Windows API 가 underscore 로 변환 안 함).
    private static readonly string[] Buttons =
    {
        "접근 허용(&A)", "접근 허용",
        "확인(&O)", "확인", "OK",
        "아니오(&N)", "예(&Y)",
        "취소(&C)", "취소",
        "저장(&Y)", "저장",
    };

    public static void Start(CancellationToken ct)
    {
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { EnumWindows(OnWindow, IntPtr.Zero); }
                catch { /* shutdown 시점의 race 무시 */ }
                try { await Task.Delay(300, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    private static bool OnWindow(IntPtr hwnd, IntPtr _)
    {
        if (!IsWindowVisible(hwnd)) return true;
        try { EnumChildWindows(hwnd, OnChild, IntPtr.Zero); }
        catch { /* 일부 window 는 enum 도중 destroy 가능 */ }
        return true;
    }

    private static bool OnChild(IntPtr hwnd, IntPtr _)
    {
        try
        {
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() != "Button") return true;

            var txt = new StringBuilder(256);
            GetWindowText(hwnd, txt, txt.Capacity);
            var t = txt.ToString();
            if (string.IsNullOrEmpty(t)) return true;

            foreach (var b in Buttons)
            {
                if (t.Contains(b, StringComparison.Ordinal))
                {
                    SendMessage(hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    break;
                }
            }
        }
        catch { /* 개별 child 실패 무시 */ }
        return true;
    }

    // ─ P/Invoke ─────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
