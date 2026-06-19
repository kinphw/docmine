// ReportBuilder — diff 결과(구조/평문)를 색상 입힌 ReportDoc 으로 변환.
//
// 색상 규약: 삭제=빨강, 추가=초록, 수정 태그=주황, 표 태그=파랑, 메타/위치=회색.
// 워커는 이 런들을 그대로 글자색 지정 + InsertText 로 찍는다.

using System.Text;

namespace DocMine.Core.Diff;

public static class ReportBuilder
{
    // RGB 팔레트
    private static readonly (int R, int G, int B) Black = (0x20, 0x20, 0x20);
    private static readonly (int R, int G, int B) Red   = (0xC0, 0x28, 0x28);
    private static readonly (int R, int G, int B) Green = (0x18, 0x80, 0x40);
    private static readonly (int R, int G, int B) Amber = (0xB0, 0x6A, 0x00);
    private static readonly (int R, int G, int B) Blue  = (0x2B, 0x5C, 0xB0);
    private static readonly (int R, int G, int B) Grey  = (0x80, 0x80, 0x80);

    /// <summary>구조 비교 결과 → 변경 목록 리포트.</summary>
    public static ReportDoc FromStructure(StructureDiff diff, string oldPath, string newPath, string summary)
    {
        var b = new Builder();
        b.Title("문서 변경 리포트 (구조 비교)");
        Meta(b, oldPath, newPath, summary);

        if (diff.Changes.Count == 0)
        {
            b.Line(("차이 없음 — 두 문서의 구조(문단·표)가 동일합니다.", Grey, false));
            return b.Build();
        }

        foreach (var ch in diff.Changes)
        {
            var (tag, tagColor) = ch.Kind switch
            {
                StructChangeKind.Inserted          => ("● 추가",  Green),
                StructChangeKind.Deleted           => ("● 삭제",  Red),
                StructChangeKind.ModifiedParagraph => ("● 수정",  Amber),
                StructChangeKind.ModifiedTable     => ("● 표변경", Blue),
                _                                  => ("● ?",    Grey),
            };
            b.Line((tag, tagColor, true), ("  " + ch.Location, Black, false));

            switch (ch.Kind)
            {
                case StructChangeKind.Inserted:
                    b.Line(("   + " + (ch.NewText ?? ""), Green, false));
                    break;
                case StructChangeKind.Deleted:
                    b.Line(("   - " + (ch.OldText ?? ""), Red, false));
                    break;
                case StructChangeKind.ModifiedParagraph:
                    b.Line(("   - " + (ch.OldText ?? ""), Red, false));
                    b.Line(("   + " + (ch.NewText ?? ""), Green, false));
                    break;
                case StructChangeKind.ModifiedTable:
                    b.Line(($"   표 크기 {ch.OldDims} → {ch.NewDims}", Grey, false));
                    if (ch.Cells is { Count: > 0 })
                        foreach (var c in ch.Cells)
                            b.Line(($"   [{c.Row + 1}행 {c.Col + 1}열]  ", Black, false),
                                   (Cell(c.Old), Red, false),
                                   ("  →  ", Black, false),
                                   (Cell(c.New), Green, false));
                    break;
            }
            b.Blank();
        }
        return b.Build();

        static string Cell(string s) => s.Length == 0 ? "(빈칸)" : s.Replace("\n", " ");
    }

    /// <summary>변경추적 통합본 → 문서 전체를 한 흐름으로(삭제=취소선, 추가=밑줄).</summary>
    public static ReportDoc FromUnified(UnifiedDiff diff, string oldPath, string newPath, string summary)
    {
        var doc = new ReportDoc();
        void Run(string text, (int R, int G, int B) c, bool bold = false, bool strike = false, bool underline = false, int breaks = 0)
            => doc.Runs.Add(new ReportRun { Text = text, R = c.R, G = c.G, B = c.B, Bold = bold, Strike = strike, Underline = underline, Breaks = breaks });

        Run("문서 변경 리포트 (변경추적)", Black, bold: true, breaks: 2);
        Run("변경 전: " + oldPath, Grey, breaks: 1);
        Run("변경 후: " + newPath, Grey, breaks: 1);
        Run(summary, Black, bold: true, breaks: 1);
        Run("범례:  삭제=취소선(빨강)  ·  추가=밑줄(초록)", Grey, breaks: 1);
        Run("────────────────────────────────────────", Grey, breaks: 2);

        foreach (var it in diff.Items)
        {
            if (it.Kind == UnifiedItemKind.Paragraph)
            {
                if (it.Runs.Count == 0) { Run("", Black, breaks: 1); continue; }
                for (int i = 0; i < it.Runs.Count; i++)
                {
                    var pr = it.Runs[i];
                    var (color, strike, underline) = StyleOf(pr.Kind);
                    Run(pr.Text, color, strike: strike, underline: underline,
                        breaks: i == it.Runs.Count - 1 ? 1 : 0);
                }
            }
            else // 표
            {
                switch (it.LineKind)
                {
                    case DiffChangeKind.Unchanged:
                        Run($"［표 {it.OldDims}］", Grey, breaks: 1); break;
                    case DiffChangeKind.Inserted:
                        Run($"［표 추가 {it.NewDims}］", Green, underline: true, breaks: 1); break;
                    case DiffChangeKind.Deleted:
                        Run($"［표 삭제 {it.OldDims}］", Red, strike: true, breaks: 1); break;
                    case DiffChangeKind.Modified:
                        Run($"［표 변경 {it.OldDims} → {it.NewDims}］", Blue, bold: true, breaks: 1);
                        if (it.Cells is { Count: > 0 })
                            foreach (var c in it.Cells)
                            {
                                Run($"   [{c.Row + 1}행 {c.Col + 1}열]  ", Black);
                                Run(Cell(c.Old), Red, strike: true);
                                Run("  →  ", Black);
                                Run(Cell(c.New), Green, underline: true, breaks: 1);
                            }
                        break;
                }
            }
        }
        return doc;

        static ((int R, int G, int B) color, bool strike, bool underline) StyleOf(DiffChangeKind k) => k switch
        {
            DiffChangeKind.Deleted  => (Red, true, false),
            DiffChangeKind.Inserted => (Green, false, true),
            _                       => (Black, false, false),
        };
        static string Cell(string s) => s.Length == 0 ? "(빈칸)" : s.Replace("\n", " ");
    }

    /// <summary>평문 좌우대조 결과 → 변경 줄만 모은 리포트(추가/삭제/수정).</summary>
    public static ReportDoc FromText(SideBySideDiff diff, string oldPath, string newPath, string summary)
    {
        var b = new Builder();
        b.Title("문서 변경 리포트 (평문)");
        Meta(b, oldPath, newPath, summary);

        int n = Math.Min(diff.Left.Count, diff.Right.Count);
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            var l = diff.Left[i];
            var r = diff.Right[i];
            switch (r.Kind)
            {
                case DiffChangeKind.Inserted:
                    b.Line(("+ " + r.Text, Green, false)); any = true; break;
                case DiffChangeKind.Modified:
                    b.Line(("- " + l.Text, Red, false));
                    b.Line(("+ " + r.Text, Green, false)); any = true; break;
                case DiffChangeKind.Unchanged:
                    break;
            }
            if (r.Kind != DiffChangeKind.Modified && l.Kind == DiffChangeKind.Deleted)
            {
                b.Line(("- " + l.Text, Red, false)); any = true;
            }
        }
        if (!any) b.Line(("차이 없음 — 두 문서의 본문 텍스트가 동일합니다.", Grey, false));
        return b.Build();
    }

    private static void Meta(Builder b, string oldPath, string newPath, string summary)
    {
        b.Line(("변경 전: " + oldPath, Grey, false));
        b.Line(("변경 후: " + newPath, Grey, false));
        b.Line((summary, Black, true));
        b.Line(("────────────────────────────────────────", Grey, false));
        b.Blank();
    }

    // ─ 작은 빌더 ────────────────────────────────────────────────────────
    private sealed class Builder
    {
        private readonly ReportDoc _doc = new();

        public void Title(string text)
        {
            _doc.Runs.Add(new ReportRun { Text = text, R = 0x20, G = 0x20, B = 0x20, Bold = true, Breaks = 2 });
        }

        // 한 줄 = 여러 색 조각, 마지막에 문단 나눔 1.
        public void Line(params (string Text, (int R, int G, int B) Color, bool Bold)[] segs)
        {
            for (int i = 0; i < segs.Length; i++)
            {
                var (text, color, bold) = segs[i];
                _doc.Runs.Add(new ReportRun
                {
                    Text = text, R = color.R, G = color.G, B = color.B,
                    Bold = bold, Breaks = i == segs.Length - 1 ? 1 : 0,
                });
            }
        }

        public void Blank() => _doc.Runs.Add(new ReportRun { Text = "", Breaks = 1 });

        public ReportDoc Build() => _doc;
    }
}
