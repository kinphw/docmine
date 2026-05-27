// LogPane — Python unified_gui.py 의 _LogPane 등가.
//
// 콘솔 진행바 (\r 또는 partial 라인) 를 단일 라이브 라인으로 갱신하는 패턴을
// WinForms RichTextBox 에 옮긴 것. UI 스레드 외에서도 안전하게 호출 가능
// (Append/AppendLine 내부에서 BeginInvoke 로 marshalling).
//
// Mirror-to-file:
//   MirrorPath 설정 시 AppendLine 호출마다 같은 메시지를 disk file 에 append
//   (immediate flush). 메인 GUI 가 silent crash 로 죽어도 그 직전까지의 메시지가
//   디스크에 남아 박건영님이 사후 확인 가능. PdfInsertTab / HwpInsertTab 처럼
//   process crash 위험 있는 작업에서 활성화.

using System.Drawing;
using System.Text;

namespace DocMine.UI;

public sealed class LogPane : UserControl
{
    private readonly RichTextBox _box;
    // 라이브 라인 시작 위치 — \r/partial 메시지가 이 지점부터 덮어씀.
    // 새 줄이 들어올 때마다 텍스트 끝으로 이동.
    private int _liveStart;

    // crash 직전 메시지 보존용 — 동시 append 안전 위해 lock.
    private string? _mirrorPath;
    private readonly object _mirrorLock = new();

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

    /// <summary>
    /// 활성화 시 AppendLine 마다 file 에 동시 append (immediate flush).
    /// path = null 로 호출 시 비활성. 같은 path 로 여러 번 호출되면 append 모드 그대로.
    /// </summary>
    public void EnableMirror(string? path)
    {
        lock (_mirrorLock) { _mirrorPath = path; }
    }

    /// <summary>새 줄 추가 (\n 등가). 라이브 라인 마크가 텍스트 끝으로 이동.</summary>
    public void AppendLine(string text)
    {
        // BeginInvoke 가 AppendLine 을 재귀 호출하므로 WriteToMirror 가 두 번
        // 호출되어 mirror file 에 중복 기록되는 버그가 있었음. UI thread 진입 후
        // 한 번만 호출하도록 InvokeRequired 분기를 먼저.
        if (InvokeRequired) { BeginInvoke(() => AppendLine(text)); return; }

        WriteToMirror(text);

        // 기존 라이브 라인을 확정한 뒤 줄바꿈.
        ReplaceLiveRegion(text + Environment.NewLine);
        _liveStart = _box.TextLength;
        _box.SelectionStart = _liveStart;
        _box.ScrollToCaret();
    }

    /// <summary>같은 라인 덮어쓰기 — \r 진행바 패턴. mirror 안 함 (디스크 부담).</summary>
    public void UpdateLive(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => UpdateLive(text)); return; }

        ReplaceLiveRegion(text);
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
    }

    public void Clear()
    {
        if (InvokeRequired) { BeginInvoke(Clear); return; }
        _box.Clear();
        _liveStart = 0;
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

    private void WriteToMirror(string text)
    {
        string? path;
        lock (_mirrorLock) { path = _mirrorPath; }
        if (path is null) return;
        try
        {
            // 매 호출 flush — process 가 즉시 죽어도 마지막 줄까지 디스크에.
            lock (_mirrorLock)
            {
                File.AppendAllText(path,
                    $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { /* mirror 실패해도 본 동작 영향 없게 */ }
    }
}
