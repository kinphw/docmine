// CF_HDROP 파일 클립보드 — Python search_gui.py _copy_files_to_clipboard 등가.
//
// 운영망 DRM 이 프로세스 basename(python.exe) 기준으로 DoDragDrop 을 막는
// 환경에서 같은 기능을 클립보드 경로로 제공한다. Explorer 는 CF_HDROP 포맷
// 클립보드를 인식해 Ctrl+V 시 파일 복사로 처리.
//
// DRM 솔루션에 따라 OleSetClipboard 도 함께 후킹돼 막힐 수 있는데, 그건
// 코드 차원의 우회 한계 — 솔루션 정책에 달려 있다 (Python 판도 동일 제약).
//
// .NET 의 Clipboard.SetFileDropList 가 같은 일을 하지만, 그 구현은 OLE 를
// 통해 가는 경우가 있어 DRM 후킹에 더 잡힐 가능성이 있다. 안전하게
// Win32 SetClipboardData(CF_HDROP) 로 직접 마샬링.

using System.Runtime.InteropServices;
using System.Text;

namespace DocMine.Win32;

public static class FileClipboard
{
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;

    /// <summary>
    /// 파일 경로 리스트를 Explorer 호환 CF_HDROP 포맷으로 클립보드에 올림.
    /// 호출 직전에 OpenClipboard / EmptyClipboard / SetClipboardData / CloseClipboard
    /// 의 표준 시퀀스를 따른다.
    /// </summary>
    /// <returns>성공 여부. DRM 환경에서 SetClipboardData 가 실패할 수 있어 bool.</returns>
    public static bool SetFileDropList(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;

        // DROPFILES 구조체 = 헤더 20바이트 + 파일 경로 UTF-16(double-null 종료).
        //   DWORD pFiles=20, POINT pt=(0,0), BOOL fNC=0, BOOL fWide=1
        var sb = new StringBuilder();
        foreach (var p in paths) { sb.Append(p); sb.Append('\0'); }
        sb.Append('\0');
        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var totalSize = 20 + bytes.Length;

        var hMem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)totalSize);
        if (hMem == IntPtr.Zero) return false;

        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero) { GlobalFree(hMem); return false; }

        try
        {
            // DROPFILES 헤더 작성.
            Marshal.WriteInt32(ptr, 0,  20);   // pFiles
            Marshal.WriteInt32(ptr, 4,  0);    // pt.x
            Marshal.WriteInt32(ptr, 8,  0);    // pt.y
            Marshal.WriteInt32(ptr, 12, 0);    // fNC
            Marshal.WriteInt32(ptr, 16, 1);    // fWide = TRUE (UTF-16)
            Marshal.Copy(bytes, 0, IntPtr.Add(ptr, 20), bytes.Length);
        }
        finally
        {
            GlobalUnlock(hMem);
        }

        if (!OpenClipboard(IntPtr.Zero))
        {
            GlobalFree(hMem);
            return false;
        }
        try
        {
            EmptyClipboard();
            // SetClipboardData 가 성공하면 시스템이 hMem 의 소유권을 가져감 → GlobalFree 호출 금지.
            if (SetClipboardData(CF_HDROP, hMem) == IntPtr.Zero)
            {
                GlobalFree(hMem);
                return false;
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
}
