// LogPane — Python unified_gui.py 의 _LogPane 등가.
//
// 콘솔 진행바 (\r 또는 partial 라인) 를 단일 라이브 라인으로 갱신하는 패턴을
// WinForms RichTextBox 에 옮긴 것. UI 스레드 외에서도 안전하게 호출 가능
// (Append/AppendLine 내부에서 BeginInvoke 로 marshalling).

using System.Drawing;
using System.Runtime.InteropServices;

namespace DocMine.UI;

public sealed class LogPane : UserControl
{
    // 텍스트 치환 중 RichTextBox 의 자체 스크롤 점프를 화면에 노출하지 않기 위한 그리기 잠금.
    private const int WM_SETREDRAW = 0x000B;

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private readonly RichTextBox _box;
    // 라이브 라인 시작 위치 — \r/partial 메시지가 이 지점부터 덮어씀.
    // 새 줄이 들어올 때마다 텍스트 끝으로 이동.
    private int _liveStart;

    public LogPane()
    {
        _box = new RichTextBox
        {
            Dock        = DockStyle.Fill,
            ReadOnly    = true,
            BackColor   = Color.FromArgb(0x1e, 0x1e, 0x1e),
            ForeColor   = Color.FromArgb(0xd4, 0xd4, 0xd4),
            Font        = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.None,
            WordWrap    = true,
            HideSelection = true,
            DetectUrls    = false,
        };
        Controls.Add(_box);
    }

    /// <summary>새 줄 추가 (\n 등가). 라이브 라인 마크가 텍스트 끝으로 이동.</summary>
    public void AppendLine(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLine(text)); return; }

        SuspendDrawing();
        try
        {
            // 기존 라이브 라인을 확정한 뒤 줄바꿈.
            ReplaceLiveRegion(text + Environment.NewLine);
            _liveStart = _box.TextLength;
            _box.SelectionStart = _liveStart;
            _box.ScrollToCaret();
        }
        finally { ResumeDrawing(); }
    }

    /// <summary>같은 라인 덮어쓰기 — \r 진행바 패턴.</summary>
    public void UpdateLive(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => UpdateLive(text)); return; }

        SuspendDrawing();
        try
        {
            ReplaceLiveRegion(text);
            _box.SelectionStart = _box.TextLength;
            _box.ScrollToCaret();
        }
        finally { ResumeDrawing(); }
    }

    public void Clear()
    {
        if (InvokeRequired) { BeginInvoke(Clear); return; }
        _box.Clear();
        _liveStart = 0;
    }

    // ── 그리기 잠금 ──────────────────────────────────────────────────
    //   Select/SelectedText 치환은 RichTextBox 가 선택 시작 위치(라이브 라인 머리)로
    //   한 번 스크롤한 뒤 ScrollToCaret 으로 끝으로 되돌아오게 만든다. 잠그지 않으면
    //   그 '위→아래' 점프가 매 갱신마다 화면에 보여 로그창이 떨린다. 구간 전체를
    //   WM_SETREDRAW 로 묶어 중간 상태를 숨기고, 마지막에 한 번만 다시 그린다.
    private void SuspendDrawing()
    {
        if (_box.IsHandleCreated) SendMessage(_box.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
    }

    private void ResumeDrawing()
    {
        if (!_box.IsHandleCreated) return;
        SendMessage(_box.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
        _box.Invalidate();
    }

    private void ReplaceLiveRegion(string newText)
    {
        // _liveStart 부터 텍스트 끝까지를 삭제하고 newText 로 치환.
        // RichTextBox.Select(start, length) + SelectedText 가 가장 빠른 in-place 치환.
        var liveLen = _box.TextLength - _liveStart;
        if (liveLen < 0) liveLen = 0;
        _box.Select(_liveStart, liveLen);
        _box.SelectedText = newText;
    }
}
