// SideBySideDiffView — SideBySideDiff 를 좌우 두 패널로 렌더링하는 재사용 컨트롤.
//
// 핵심:
//   ㆍ DiffPlex 가 양쪽을 Imaginary 패딩으로 같은 줄 수에 맞춰 주므로, WordWrap 을
//     끄면 '1 논리 줄 = 1 화면 줄' 이 보장돼 두 박스의 세로 스크롤을 줄 번호로
//     정확히 동기화할 수 있다 (EM_LINESCROLL).
//   ㆍ 라인 배경색(추가/삭제/수정/패딩) + 수정 라인의 단어 단위 진한 하이라이트.
//   ㆍ "변경된 부분만 보기" 면 변경 줄 주변 N줄만 남기고 나머지는 '⋯ k줄 동일 ⋯' 로 접음.
//
// 성능: 텍스트를 한 번에 set 한 뒤 색 span 만 Select+SelectionBackColor 로 적용
//       (줄마다 SelectedText append 하는 방식보다 훨씬 빠름). 그리는 동안 WM_SETREDRAW off.

using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using DocMine.Core.Diff;

namespace DocMine.UI.Tabs;

public sealed class SideBySideDiffView : UserControl
{
    private const int ContextLines = 3;   // "변경된 부분만" 일 때 변경 주변 보존 줄 수

    private readonly DiffTextBox _left  = new();
    private readonly DiffTextBox _right = new();
    private bool _syncing;

    public SideBySideDiffView()
    {
        var leftHeader  = MakeHeader("◀ 변경 전");
        var rightHeader = MakeHeader("변경 후 ▶");

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(leftHeader,  0, 0);
        grid.Controls.Add(rightHeader, 1, 0);
        grid.Controls.Add(_left,  0, 1);
        grid.Controls.Add(_right, 1, 1);
        Controls.Add(grid);

        _left.UserScrolled  += () => Mirror(_left, _right);
        _right.UserScrolled += () => Mirror(_right, _left);
    }

    private static Label MakeHeader(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Height = 22,
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("맑은 고딕", 9f, FontStyle.Bold),
        ForeColor = Color.FromArgb(0x30, 0x30, 0x30),
        BackColor = Color.FromArgb(0xEC, 0xEC, 0xEC),
    };

    public void Clear()
    {
        _left.Clear();
        _right.Clear();
    }

    public void Render(SideBySideDiff diff, bool changedOnly)
    {
        var rows = BuildRows(diff, changedOnly);
        RenderPane(_left,  rows, isLeft: true);
        RenderPane(_right, rows, isLeft: false);
    }

    // ─ 행 구성 ──────────────────────────────────────────────────────────
    // 콘텐츠 행(Left/Right 같은 인덱스 짝) 또는 접힘 행(Gap=동일 줄 수).

    private readonly record struct RenderRow(DiffLine? Left, DiffLine? Right, int Gap);

    private static List<RenderRow> BuildRows(SideBySideDiff diff, bool changedOnly)
    {
        int n = Math.Min(diff.Left.Count, diff.Right.Count);
        var rows = new List<RenderRow>(n);

        if (!changedOnly)
        {
            for (int i = 0; i < n; i++)
                rows.Add(new RenderRow(diff.Left[i], diff.Right[i], 0));
            return rows;
        }

        var keep = new bool[n];
        for (int i = 0; i < n; i++)
        {
            bool changed = diff.Left[i].Kind  != DiffChangeKind.Unchanged
                        || diff.Right[i].Kind != DiffChangeKind.Unchanged;
            if (!changed) continue;
            for (int j = Math.Max(0, i - ContextLines); j <= Math.Min(n - 1, i + ContextLines); j++)
                keep[j] = true;
        }

        int k = 0;
        while (k < n)
        {
            if (keep[k])
            {
                rows.Add(new RenderRow(diff.Left[k], diff.Right[k], 0));
                k++;
            }
            else
            {
                int start = k;
                while (k < n && !keep[k]) k++;
                rows.Add(new RenderRow(null, null, k - start));
            }
        }
        return rows;
    }

    // ─ 한 패널 렌더 ─────────────────────────────────────────────────────

    private static void RenderPane(DiffTextBox box, List<RenderRow> rows, bool isLeft)
    {
        var sb = new StringBuilder();
        var lineBg  = new List<(int Start, int Len, Color Color)>();
        var pieceBg = new List<(int Start, int Len, Color Color)>();
        var gapFg   = new List<(int Start, int Len)>();

        foreach (var row in rows)
        {
            if (row.Gap > 0)
            {
                int gs = sb.Length;
                string text = $"⋯ {row.Gap:N0}줄 동일 ⋯";
                sb.Append(text).Append('\n');
                gapFg.Add((gs, text.Length));
                continue;
            }

            var line = isLeft ? row.Left! : row.Right!;
            int lineStart = sb.Length;
            var baseColor = BaseBg(line.Kind, isLeft);

            if (line.Kind == DiffChangeKind.Modified && line.Pieces.Count > 0)
            {
                foreach (var pc in line.Pieces)
                {
                    if (pc.Kind == DiffChangeKind.Imaginary) continue;  // 패딩 조각은 텍스트 없음
                    int ps = sb.Length;
                    sb.Append(pc.Text);
                    var pcColor = PieceBg(pc.Kind, isLeft);
                    if (pcColor.HasValue && pc.Text.Length > 0)
                        pieceBg.Add((ps, pc.Text.Length, pcColor.Value));
                }
            }
            else
            {
                sb.Append(line.Text);
            }

            int lineLen = sb.Length - lineStart;
            sb.Append('\n');
            if (baseColor.HasValue && lineLen > 0)
                lineBg.Add((lineStart, lineLen, baseColor.Value));
        }

        SetRedraw(box, false);
        try
        {
            box.Clear();
            box.Text = sb.ToString();

            // 라인 배경 먼저 → 그 위에 단어 조각 배경을 덮어쓴다.
            foreach (var (start, len, color) in lineBg)
            {
                box.Select(start, len);
                box.SelectionBackColor = color;
            }
            foreach (var (start, len, color) in pieceBg)
            {
                box.Select(start, len);
                box.SelectionBackColor = color;
            }
            foreach (var (start, len) in gapFg)
            {
                box.Select(start, len);
                box.SelectionColor = GapFg;
            }
            box.Select(0, 0);
        }
        finally
        {
            SetRedraw(box, true);
            box.Invalidate();
        }
    }

    // ─ 색상 ────────────────────────────────────────────────────────────

    private static readonly Color BgDelete       = Color.FromArgb(0xFF, 0xE0, 0xE0);
    private static readonly Color BgInsert        = Color.FromArgb(0xE0, 0xFF, 0xE0);
    private static readonly Color BgDeleteStrong  = Color.FromArgb(0xFF, 0xAC, 0xAC);
    private static readonly Color BgInsertStrong  = Color.FromArgb(0xA8, 0xEA, 0xA8);
    private static readonly Color BgImaginary     = Color.FromArgb(0xF2, 0xF2, 0xF2);
    private static readonly Color GapFg           = Color.FromArgb(0x90, 0x90, 0x90);

    private static Color? BaseBg(DiffChangeKind k, bool isLeft) => k switch
    {
        DiffChangeKind.Deleted   => BgDelete,
        DiffChangeKind.Inserted  => BgInsert,
        DiffChangeKind.Modified  => isLeft ? BgDelete : BgInsert,
        DiffChangeKind.Imaginary => BgImaginary,
        _                        => null,   // Unchanged → 기본 흰 배경
    };

    private static Color? PieceBg(DiffChangeKind k, bool isLeft) => k switch
    {
        DiffChangeKind.Deleted  => BgDeleteStrong,
        DiffChangeKind.Inserted => BgInsertStrong,
        _                       => null,    // Unchanged 조각 → 라인 배경 유지
    };

    // ─ 세로 스크롤 동기화 ───────────────────────────────────────────────

    private void Mirror(DiffTextBox from, DiffTextBox to)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            int srcLine = FirstVisibleLine(from);
            int dstLine = FirstVisibleLine(to);
            int delta = srcLine - dstLine;
            if (delta != 0)
                SendMessage(to.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)delta);
        }
        finally { _syncing = false; }
    }

    private static int FirstVisibleLine(RichTextBox box)
        => box.GetLineFromCharIndex(box.GetCharIndexFromPosition(new Point(1, 1)));

    private const int EM_LINESCROLL = 0x00B6;
    private const int WM_SETREDRAW  = 0x000B;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void SetRedraw(Control c, bool on)
    {
        if (c.IsHandleCreated)
            SendMessage(c.Handle, WM_SETREDRAW, (IntPtr)(on ? 1 : 0), IntPtr.Zero);
    }

    // ─ 스크롤 이벤트를 노출하는 RichTextBox ──────────────────────────────

    private sealed class DiffTextBox : RichTextBox
    {
        public event Action? UserScrolled;

        private const int WM_VSCROLL    = 0x0115;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_KEYUP      = 0x0101;

        public DiffTextBox()
        {
            ReadOnly      = true;
            WordWrap      = false;            // 1 논리 줄 = 1 화면 줄 (스크롤 동기화 전제)
            BorderStyle   = BorderStyle.None;
            ScrollBars    = RichTextBoxScrollBars.Both;
            BackColor     = Color.White;
            ForeColor     = Color.FromArgb(0x20, 0x20, 0x20);
            Font          = new Font("Consolas", 10f);  // Windows 기본 탑재 등폭 (LogPane 과 동일)
            HideSelection = true;
            DetectUrls    = false;
            Dock          = DockStyle.Fill;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg is WM_VSCROLL or WM_MOUSEWHEEL or WM_KEYUP)
                UserScrolled?.Invoke();
        }
    }
}
