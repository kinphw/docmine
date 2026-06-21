// FileDropBox — 파일을 "끌어다 놓는" 것이 한눈에 보이는 큰 입력 박스.
//
// CompareTab(문서 비교)의 변경 전/후 입력은 원래 한 줄 텍스트박스 + [찾아보기] 였는데,
// 좁은 입력칸이라 드래그 대상으로 보이지 않아 사용자가 매번 [찾아보기]로만 골랐다.
// 이 컨트롤은 점선 테두리의 넓은 영역 + 안내문으로 "여기로 끌어다 놓아라"를 분명히 하고,
// 클릭하면 찾아보기 다이얼로그를 연다. 한 박스에 2개를 떨구면 MultiDropped 로 짝을 알려
// 변경 전/후를 한꺼번에 채울 수 있다.
//
// 그림은 모두 OnPaint 로 직접 그린다(자식 라벨 없음) — 드래그/클릭 히트 영역이 박스 전체로
// 하나가 되어 자식 컨트롤 위에서 이벤트가 새는 문제를 피한다.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DocMine.UI.Tabs;

public sealed class FileDropBox : Panel
{
    private static readonly Color BorderIdle   = Color.FromArgb(170, 170, 170);
    private static readonly Color BorderHover  = Color.FromArgb(0, 120, 215);
    private static readonly Color BorderOff    = Color.FromArgb(210, 210, 210);
    private static readonly Color FillIdle     = Color.FromArgb(250, 250, 250);
    private static readonly Color FillHover    = Color.FromArgb(232, 242, 254);
    private static readonly Color FillOff      = Color.FromArgb(244, 244, 244);
    private static readonly Color CaptionColor = Color.FromArgb(60, 60, 60);
    private static readonly Color HintColor    = Color.FromArgb(140, 140, 140);
    private static readonly Color NameColor    = Color.FromArgb(20, 20, 20);

    private readonly Font _captionFont;
    private readonly Font _nameFont;
    private readonly Font _hintFont;
    private readonly ToolTip _tip = new() { ShowAlways = true };

    private string _path = "";
    private bool _dragOver;

    /// <summary>박스 제목(예: "변경 전").</summary>
    public string Caption { get; init; } = "";

    /// <summary>비어 있을 때 가운데에 보여줄 안내문.</summary>
    public string Hint { get; init; } = "파일을 여기로 끌어다 놓거나, 클릭해서 선택하세요";

    /// <summary>찾아보기 다이얼로그 필터.</summary>
    public string DialogFilter { get; init; } = "모든 파일 (*.*)|*.*";

    /// <summary>드롭/선택 파일이 유효한지 검사(확장자 등). null 이면 모두 허용.</summary>
    public Func<string, bool>? Accept { get; init; }

    /// <summary>현재 선택된 경로. 빈 문자열이면 미선택.</summary>
    public string Path
    {
        get => _path;
        set
        {
            var v = value?.Trim() ?? "";
            if (v == _path) return;
            _path = v;
            _tip.SetToolTip(this, v.Length == 0 ? null : v);
            Invalidate();
            PathChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>경로가 바뀔 때.</summary>
    public event EventHandler? PathChanged;

    /// <summary>한 박스에 2개 이상 떨궜을 때 — 짝 박스까지 채우게 알린다(유효 파일만 전달).</summary>
    public event EventHandler<string[]>? MultiDropped;

    public FileDropBox()
    {
        DoubleBuffered = true;
        AllowDrop = true;
        Cursor = Cursors.Hand;
        Margin = new Padding(4);
        MinimumSize = new Size(0, 96);
        SetStyle(ControlStyles.ResizeRedraw, true);
        _captionFont = new Font("맑은 고딕", 10.5f, FontStyle.Bold);
        _nameFont    = new Font("맑은 고딕", 10f);
        _hintFont    = new Font("맑은 고딕", 9f);
    }

    // ── 관리자 권한 실행 대비 ───────────────────────────────────────────
    // DRM 운영 환경에서 본 프로그램이 관리자 권한으로 뜨면, 일반 권한인 탐색기에서
    // 오는 드롭 메시지가 UIPI 로 막힌다(드래그가 "안 되는" 흔한 원인). 이 창에 한해
    // 해당 메시지를 명시적으로 허용한다. 일반 권한 실행 시엔 영향 없음(no-op).
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            ChangeWindowMessageFilterEx(Handle, WM_DROPFILES,      MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(Handle, WM_COPYDATA,       MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(Handle, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
        }
        catch { /* 구버전/실패는 무시 — 보조 안전망 */ }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && Enabled) Browse();
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = (Caption.Length > 0 ? Caption + " " : "") + "문서 선택",
            Filter = DialogFilter,
        };
        if (_path.Length > 0)
        {
            try { dlg.InitialDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path)); } catch { }
        }
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (Accept is null || Accept(dlg.FileName)) Path = dlg.FileName;
        else MessageBox.Show(FindForm(), "지원하지 않는 형식입니다.", "형식 오류",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        bool ok = Enabled && e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok && !_dragOver) { _dragOver = true; Invalidate(); }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        if (_dragOver) { _dragOver = false; Invalidate(); }
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        _dragOver = false;
        Invalidate();
        if (!Enabled) return;
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        var valid = (Accept is null ? files : files.Where(Accept)).ToArray();
        if (valid.Length == 0)
        {
            MessageBox.Show(FindForm(), "지원하는 문서(.hwp / .hwpx)가 아닙니다.", "형식 오류",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Path = valid[0];
        if (valid.Length >= 2) MultiDropped?.Invoke(this, valid);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        bool on = Enabled;
        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        Color border = _dragOver ? BorderHover : on ? BorderIdle : BorderOff;
        Color fill   = _dragOver ? FillHover  : on ? FillIdle   : FillOff;

        using (var b = new SolidBrush(fill))
            g.FillRectangle(b, rect);
        using (var pen = new Pen(border, _dragOver ? 2f : 1.4f) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(pen, rect);

        // 캡션(좌상단).
        TextRenderer.DrawText(g, Caption, _captionFont, new Point(12, 8), CaptionColor);

        var body = new Rectangle(12, 32, Width - 24, Height - 40);
        if (_path.Length == 0)
        {
            TextRenderer.DrawText(g, Hint, _hintFont, body, HintColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
        else
        {
            var name = System.IO.Path.GetFileName(_path);
            var dir  = System.IO.Path.GetDirectoryName(_path) ?? "";
            TextRenderer.DrawText(g, name, _nameFont, body, NameColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordEllipsis | TextFormatFlags.SingleLine);
            var dirRect = new Rectangle(body.X, body.Y + 24, body.Width, Math.Max(0, body.Height - 24));
            TextRenderer.DrawText(g, dir, _hintFont, dirRect, HintColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _captionFont.Dispose();
            _nameFont.Dispose();
            _hintFont.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }

    // ── Win32 ───────────────────────────────────────────────────────────
    private const uint WM_DROPFILES      = 0x0233;
    private const uint WM_COPYDATA       = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW      = 1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeInfo);
}
