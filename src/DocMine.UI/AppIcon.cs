// 앱 아이콘 — Python icon.py:make_app_icon 의 1:1 포팅.
//
// 64×64 'M' 글리프 (좌·우 다리 + 중앙으로 내려오는 4단 계단) 를 Bitmap 에
// 픽셀 단위로 그리고 Icon 으로 변환. 외부 .ico 파일 의존 없음 — 단일 binary
// 배포 그대로.

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DocMine.UI;

public static class AppIcon
{
    private static readonly Color Bg = Color.FromArgb(0x1f, 0x29, 0x37);   // slate-800
    private static readonly Color Fg = Color.FromArgb(0x14, 0xb8, 0xa6);   // teal-500

    /// <summary>
    /// MainForm.Icon / Form.ShowIcon 에 사용. 생성된 Icon 은 GC 후에도 살아있도록
    /// 참조 보관 권장 (Form.Icon 이 자동 dispose 안 함).
    /// </summary>
    public static Icon Build()
    {
        const int size = 64;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Bg);
            using var brush = new SolidBrush(Fg);
            // 좌측 다리
            g.FillRectangle(brush, 10, 12, 8, 40);
            // 우측 다리
            g.FillRectangle(brush, 46, 12, 8, 40);
            // 중앙으로 내려오는 4단 계단
            for (int i = 0; i < 4; i++)
            {
                int y = 12 + i * 4;
                g.FillRectangle(brush, 18 + i * 4, y, 4, 4);   // 좌 → 중앙
                g.FillRectangle(brush, 42 - i * 4, y, 4, 4);   // 우 → 중앙
            }
        }

        // Bitmap → Icon. GetHicon 의 HICON 은 별도 DestroyIcon 필요하지만
        // Form 의 Icon 으로 평생 사용하므로 release 안 함 (앱 종료 시 OS 회수).
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
