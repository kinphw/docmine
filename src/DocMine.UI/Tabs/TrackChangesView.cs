// TrackChangesView — 변경추적(track-changes) 단일 뷰.
//
// 변경 전/후를 한 흐름으로 병합해, 파일을 열지 않고도 화면에서 전체 문서를 읽으며
// 변경점을 따라갈 수 있게 한다 (워드/한글 변경내용추적의 "모든 변경 내용" 보기와 동형).
//   ㆍ 동일 = 기본색      ㆍ 삭제 = 빨강 + 취소선      ㆍ 추가 = 초록 + 밑줄
//   ㆍ 표는 ［표 변경 R×C → R×C］ + 셀별 old → new 로 요약.
//
// 성능: 텍스트를 한 번에 set 한 뒤 변경 조각에만 색/서체 span 을 입힌다(동일 텍스트는
//       기본색이라 span 불필요). 변경이 과도하면(무관 문서 등) 색 span 예산을 넘기는
//       시점에서 색칠만 멈추고 본문은 끝까지 보여준다 — UI 멈춤 방지.

using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using DocMine.Core.Diff;

namespace DocMine.UI.Tabs;

public sealed class TrackChangesView : UserControl
{
    // 색 span(서체 변경 포함) 예산 — 초과 시 색칠 중단(본문은 계속 출력).
    private const int MaxStyledSpans = 6000;

    private readonly RichTextBox _box;
    private readonly Font _baseFont;
    private readonly Font _strikeFont;
    private readonly Font _underlineFont;
    private bool _capped;

    public TrackChangesView()
    {
        _baseFont      = new Font("맑은 고딕", 10.5f);
        _strikeFont    = new Font(_baseFont, FontStyle.Strikeout);
        _underlineFont = new Font(_baseFont, FontStyle.Underline);

        _box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Unchanged,
            Font = _baseFont,
            HideSelection = true,
            DetectUrls = false,
        };
        Controls.Add(_box);
    }

    /// <summary>색칠 예산을 넘겨 일부만 강조된 상태인지(호출자가 안내 표기용).</summary>
    public bool WasCapped => _capped;

    public void Clear() => _box.Clear();

    public void Render(UnifiedDiff diff)
    {
        var sb = new StringBuilder();
        // (start, len, color, style)  style: 0=색만, 1=취소선, 2=밑줄
        var spans = new List<(int Start, int Len, Color Color, int Style)>();
        _capped = false;

        if (diff.Items.Count == 0)
        {
            sb.Append("차이 없음 — 두 문서가 동일합니다.");
            spans.Add((0, sb.Length, GreyFg, 0));
            Apply(sb.ToString(), spans);
            return;
        }

        foreach (var it in diff.Items)
        {
            if (it.Kind == UnifiedItemKind.Paragraph)
                AppendParagraph(sb, spans, it);
            else
                AppendTable(sb, spans, it);
        }

        Apply(sb.ToString(), spans);
    }

    // ─ 문단 ──────────────────────────────────────────────────────────────

    private void AppendParagraph(StringBuilder sb, List<(int, int, Color, int)> spans, UnifiedItem it)
    {
        if (it.Runs.Count == 0) { sb.Append('\n'); return; }

        foreach (var run in it.Runs)
        {
            if (run.Text.Length == 0) continue;
            int s = sb.Length;
            sb.Append(run.Text);
            switch (run.Kind)
            {
                case DiffChangeKind.Deleted:  AddSpan(spans, s, run.Text.Length, DeleteFg, 1); break;
                case DiffChangeKind.Inserted: AddSpan(spans, s, run.Text.Length, InsertFg, 2); break;
                // Unchanged → 기본색, span 불필요
            }
        }
        sb.Append('\n');
    }

    // ─ 표 ────────────────────────────────────────────────────────────────

    private void AppendTable(StringBuilder sb, List<(int, int, Color, int)> spans, UnifiedItem it)
    {
        switch (it.LineKind)
        {
            case DiffChangeKind.Unchanged:
                AppendLine(sb, spans, $"［표 {it.OldDims}］", GreyFg, 0);
                break;
            case DiffChangeKind.Inserted:
                AppendLine(sb, spans, $"［표 추가 {it.NewDims}］", InsertFg, 2);
                break;
            case DiffChangeKind.Deleted:
                AppendLine(sb, spans, $"［표 삭제 {it.OldDims}］", DeleteFg, 1);
                break;
            case DiffChangeKind.Modified:
                AppendLine(sb, spans, $"［표 변경 {it.OldDims} → {it.NewDims}］", TableFg, 0);
                if (it.Cells is { Count: > 0 })
                    foreach (var c in it.Cells)
                    {
                        sb.Append($"    [{c.Row + 1}행 {c.Col + 1}열]  ");
                        int os = sb.Length; var ot = Cell(c.Old); sb.Append(ot); AddSpan(spans, os, ot.Length, DeleteFg, 1);
                        sb.Append("  →  ");
                        int ns = sb.Length; var nt = Cell(c.New); sb.Append(nt); AddSpan(spans, ns, nt.Length, InsertFg, 2);
                        sb.Append('\n');
                    }
                break;
        }
    }

    private void AppendLine(StringBuilder sb, List<(int, int, Color, int)> spans, string text, Color color, int style)
    {
        int s = sb.Length;
        sb.Append(text);
        AddSpan(spans, s, text.Length, color, style);
        sb.Append('\n');
    }

    private void AddSpan(List<(int, int, Color, int)> spans, int start, int len, Color color, int style)
    {
        if (len <= 0) return;
        if (spans.Count >= MaxStyledSpans) { _capped = true; return; }
        spans.Add((start, len, color, style));
    }

    private static string Cell(string s) => s.Length == 0 ? "(빈칸)" : s.Replace("\n", " ");

    // ─ 색·서체 적용 ──────────────────────────────────────────────────────

    private void Apply(string text, List<(int Start, int Len, Color Color, int Style)> spans)
    {
        SetRedraw(_box, false);
        try
        {
            _box.Clear();
            _box.Text = text;
            foreach (var (start, len, color, style) in spans)
            {
                _box.Select(start, len);
                _box.SelectionColor = color;
                if (style == 1) _box.SelectionFont = _strikeFont;
                else if (style == 2) _box.SelectionFont = _underlineFont;
            }
            _box.Select(0, 0);
        }
        finally
        {
            SetRedraw(_box, true);
            _box.Invalidate();
        }
    }

    private static readonly Color Unchanged = Color.FromArgb(0x20, 0x20, 0x20);
    private static readonly Color DeleteFg  = Color.FromArgb(0xC0, 0x2B, 0x2B);
    private static readonly Color InsertFg  = Color.FromArgb(0x12, 0x7A, 0x33);
    private static readonly Color TableFg   = Color.FromArgb(0x2B, 0x5C, 0xB0);
    private static readonly Color GreyFg    = Color.FromArgb(0x80, 0x80, 0x80);

    private const int WM_SETREDRAW = 0x000B;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void SetRedraw(Control c, bool on)
    {
        if (c.IsHandleCreated)
            SendMessage(c.Handle, WM_SETREDRAW, (IntPtr)(on ? 1 : 0), IntPtr.Zero);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _strikeFont.Dispose();
            _underlineFont.Dispose();
            _baseFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
