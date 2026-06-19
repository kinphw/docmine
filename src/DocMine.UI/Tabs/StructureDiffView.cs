// StructureDiffView — 구조 diff(StructureDiff) 를 변경 "목록" 리포트로 렌더링.
//
// 단일 RichTextBox 에 색상 입힌 리포트를 그린다 (스크롤·복사 가능, 그대로 저장 출력과 동형).
//   ● 수정   제2조 적용범위 › 문단 5
//        - 전 직원에 적용한다.            (삭제 단어 진한 빨강)
//        + 전 임직원에게 적용한다.        (추가 단어 진한 초록)
//   ● 표변경 제3조 › 표 (3×4 → 3×4)
//        [2행 2열]  10  →  15
//
// 성능: 텍스트를 한 번에 set 한 뒤 색 span 만 Select 로 적용 (SideBySideDiffView 와 동일 기법).

using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using DocMine.Core.Diff;

namespace DocMine.UI.Tabs;

public sealed class StructureDiffView : UserControl
{
    private readonly RichTextBox _box;

    public StructureDiffView()
    {
        _box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(0x20, 0x20, 0x20),
            Font = new Font("Consolas", 10.5f),
            HideSelection = true,
            DetectUrls = false,
        };
        Controls.Add(_box);
    }

    public void Clear() => _box.Clear();

    public void Render(StructureDiff diff)
    {
        var sb = new StringBuilder();
        var bg = new List<(int Start, int Len, Color Color)>();
        var fg = new List<(int Start, int Len, Color Color, bool Bold)>();

        if (diff.Changes.Count == 0)
        {
            sb.Append("차이 없음 — 두 문서의 구조(문단·표)가 동일합니다.");
            fg.Add((0, sb.Length, GreyFg, false));
            Apply(sb.ToString(), bg, fg);
            return;
        }

        foreach (var ch in diff.Changes)
        {
            var (tag, tagColor) = TagOf(ch.Kind);

            // 헤더: ● 태그   위치
            int hs = sb.Length;
            sb.Append("● ").Append(tag);
            fg.Add((hs, sb.Length - hs, tagColor, true));
            sb.Append("  ").Append(ch.Location).Append('\n');

            switch (ch.Kind)
            {
                case StructChangeKind.Inserted:
                    AppendSign(sb, bg, "   + ", ch.NewText ?? "", InsertBg);
                    break;

                case StructChangeKind.Deleted:
                    AppendSign(sb, bg, "   - ", ch.OldText ?? "", DeleteBg);
                    break;

                case StructChangeKind.ModifiedParagraph:
                    AppendInline(sb, bg, "   - ", ch.OldLine, isLeft: true);
                    AppendInline(sb, bg, "   + ", ch.NewLine, isLeft: false);
                    break;

                case StructChangeKind.ModifiedTable:
                    int ds = sb.Length;
                    sb.Append($"   표 크기 {ch.OldDims} → {ch.NewDims}\n");
                    fg.Add((ds, sb.Length - ds - 1, GreyFg, false));
                    if (ch.Cells is { Count: > 0 })
                        foreach (var c in ch.Cells)
                            AppendCell(sb, bg, c);
                    else
                        sb.Append("   (셀 변경 없음 — 행/열 구조만 변경)\n");
                    break;
            }

            sb.Append('\n');
        }

        Apply(sb.ToString(), bg, fg);
    }

    // ─ 라인 빌더 ────────────────────────────────────────────────────────

    private static void AppendSign(StringBuilder sb, List<(int, int, Color)> bg,
        string sign, string text, Color color)
    {
        int s = sb.Length;
        sb.Append(sign).Append(text).Append('\n');
        bg.Add((s, sb.Length - s - 1, color));
    }

    // 인라인 단어 하이라이트가 있는 수정 라인 (- 전 / + 후).
    private static void AppendInline(StringBuilder sb, List<(int, int, Color)> bg,
        string sign, DiffLine? line, bool isLeft)
    {
        int s = sb.Length;
        sb.Append(sign);

        var baseColor = isLeft ? DeleteBg : InsertBg;
        var strongColor = isLeft ? DeleteStrong : InsertStrong;
        var wantPieceKind = isLeft ? DiffChangeKind.Deleted : DiffChangeKind.Inserted;

        if (line is not null && line.Pieces.Count > 0 && line.Kind == DiffChangeKind.Modified)
        {
            foreach (var pc in line.Pieces)
            {
                if (pc.Kind == DiffChangeKind.Imaginary) continue;
                int ps = sb.Length;
                sb.Append(pc.Text);
                if (pc.Kind == wantPieceKind && pc.Text.Length > 0)
                    bg.Add((ps, pc.Text.Length, strongColor));
            }
        }
        else
        {
            sb.Append(line?.Text ?? "");
        }

        int lineLen = sb.Length - s;
        sb.Append('\n');
        bg.Add((s, lineLen, baseColor));   // 라인 전체 옅은 배경 (조각 강조는 위에서 먼저 추가됨)
    }

    private static void AppendCell(StringBuilder sb, List<(int, int, Color)> bg, CellChange c)
    {
        int s = sb.Length;
        var oldT = c.Old.Length == 0 ? "(빈칸)" : c.Old.Replace("\n", " ");
        var newT = c.New.Length == 0 ? "(빈칸)" : c.New.Replace("\n", " ");
        sb.Append($"   [{c.Row + 1}행 {c.Col + 1}열]  ");
        int os = sb.Length; sb.Append(oldT); bg.Add((os, oldT.Length, DeleteBg));
        sb.Append("  →  ");
        int ns = sb.Length; sb.Append(newT); bg.Add((ns, newT.Length, InsertBg));
        sb.Append('\n');
    }

    // ─ 색 적용 ──────────────────────────────────────────────────────────

    private void Apply(string text,
        List<(int Start, int Len, Color Color)> bg,
        List<(int Start, int Len, Color Color, bool Bold)> fg)
    {
        SetRedraw(_box, false);
        try
        {
            _box.Clear();
            _box.Text = text;

            foreach (var (start, len, color) in bg)
            {
                if (len <= 0) continue;
                _box.Select(start, len);
                _box.SelectionBackColor = color;
            }
            var baseFont = _box.Font;
            var boldFont = new Font(baseFont, FontStyle.Bold);
            foreach (var (start, len, color, bold) in fg)
            {
                if (len <= 0) continue;
                _box.Select(start, len);
                _box.SelectionColor = color;
                if (bold) _box.SelectionFont = boldFont;
            }
            _box.Select(0, 0);
        }
        finally
        {
            SetRedraw(_box, true);
            _box.Invalidate();
        }
    }

    private static (string Tag, Color Color) TagOf(StructChangeKind k) => k switch
    {
        StructChangeKind.Inserted          => ("추가",  InsertFg),
        StructChangeKind.Deleted           => ("삭제",  DeleteFg),
        StructChangeKind.ModifiedParagraph => ("수정",  ModifyFg),
        StructChangeKind.ModifiedTable     => ("표변경", TableFg),
        _                                  => ("?",     GreyFg),
    };

    private static readonly Color InsertBg     = Color.FromArgb(0xE3, 0xFA, 0xE3);
    private static readonly Color DeleteBg     = Color.FromArgb(0xFD, 0xE6, 0xE6);
    private static readonly Color InsertStrong = Color.FromArgb(0xA8, 0xEA, 0xA8);
    private static readonly Color DeleteStrong = Color.FromArgb(0xFF, 0xAC, 0xAC);
    private static readonly Color InsertFg     = Color.FromArgb(0x1A, 0x7F, 0x37);
    private static readonly Color DeleteFg     = Color.FromArgb(0xC0, 0x2B, 0x2B);
    private static readonly Color ModifyFg     = Color.FromArgb(0xB0, 0x6A, 0x00);
    private static readonly Color TableFg      = Color.FromArgb(0x2B, 0x5C, 0xB0);
    private static readonly Color GreyFg       = Color.FromArgb(0x80, 0x80, 0x80);

    private const int WM_SETREDRAW = 0x000B;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void SetRedraw(Control c, bool on)
    {
        if (c.IsHandleCreated)
            SendMessage(c.Handle, WM_SETREDRAW, (IntPtr)(on ? 1 : 0), IntPtr.Zero);
    }
}
