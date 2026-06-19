// DocumentStructureComparer — 두 DocStructure 를 문단/표 단위로 비교.
//
// 알고리즘:
//   1. 각 블록을 "한 줄 키" 로 만든다 (문단=텍스트, 표=셀 평탄화). 줄바꿈은 제거해
//      한 블록이 정확히 한 줄이 되도록 보장 (정렬 무결성).
//   2. DiffPlex SideBySideDiffBuilder 로 블록 키 시퀀스를 정렬 — LCS + 인접 변경의
//      Modified 짝맞춤을 그대로 활용한다.
//   3. 정렬된 좌/우 라인을 two-pointer 로 훑으며 블록으로 되돌려 변경 항목을 만든다.
//      ㆍ 문단 수정  → 텍스트를 단어 단위로 재diff (DocumentComparer 재사용)
//      ㆍ 표 수정    → 셀 격자 비교
//      ㆍ 추가/삭제  → 원문 미리보기
//   4. 위치(Location)는 직전 제목(아웃라인 레벨>0 또는 '제N조/장/절') + 문단 번호로 구성.

using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace DocMine.Core.Diff;

public sealed class DocumentStructureComparer
{
    private readonly SideBySideDiffBuilder _builder = new(new Differ());
    private readonly DocumentComparer _inline = new();

    private static readonly Regex HeadingRe =
        new(@"^\s*제\s*\d+\s*(조|장|절|관)\b", RegexOptions.Compiled);

    public StructureDiff Compare(DocStructure oldDoc, DocStructure newDoc, bool ignoreWhitespace = true)
    {
        var oldBlocks = oldDoc.Blocks;
        var newBlocks = newDoc.Blocks;

        var oldKeys = string.Join("\n", oldBlocks.Select(BlockKey));
        var newKeys = string.Join("\n", newBlocks.Select(BlockKey));
        var model = _builder.BuildDiffModel(oldKeys, newKeys, ignoreWhitespace);

        var oldLines = model.OldText.Lines;
        var newLines = model.NewText.Lines;
        int rowCount = Math.Max(oldLines.Count, newLines.Count);

        var changes = new List<StructChange>();
        int oi = 0, ni = 0;                  // 소비한 블록 인덱스
        int oPara = 0, nPara = 0;            // 문단 일련번호(1-based)
        string oldHeading = "", newHeading = "";
        int inserted = 0, deleted = 0, modified = 0, unchanged = 0;

        for (int r = 0; r < rowCount; r++)
        {
            var oType = r < oldLines.Count ? oldLines[r].Type : ChangeType.Imaginary;
            var nType = r < newLines.Count ? newLines[r].Type : ChangeType.Imaginary;

            bool hasOld = oType != ChangeType.Imaginary && oi < oldBlocks.Count;
            bool hasNew = nType != ChangeType.Imaginary && ni < newBlocks.Count;

            DocBlock? ob = hasOld ? oldBlocks[oi] : null;
            DocBlock? nb = hasNew ? newBlocks[ni] : null;

            // 문단 일련번호 부여(소비 시점).
            int oOrd = 0, nOrd = 0;
            if (ob is { IsParagraph: true }) oOrd = ++oPara;
            if (nb is { IsParagraph: true }) nOrd = ++nPara;

            if (hasOld && hasNew)
            {
                if (oType == ChangeType.Unchanged && nType == ChangeType.Unchanged)
                {
                    unchanged++;
                }
                else if (ob!.IsParagraph && nb!.IsParagraph)
                {
                    changes.Add(MakeParagraphMod(ob, nb, Loc(newHeading, nOrd, false), ignoreWhitespace));
                    modified++;
                }
                else if (ob!.IsTable && nb!.IsTable)
                {
                    changes.Add(MakeTableMod(ob, nb, Loc(newHeading, nOrd, true)));
                    modified++;
                }
                else
                {
                    // 종류가 바뀜(문단↔표) — 삭제 + 추가로 분해.
                    changes.Add(MakeDelete(ob!, Loc(oldHeading, oOrd, ob!.IsTable)));
                    changes.Add(MakeInsert(nb!, Loc(newHeading, nOrd, nb!.IsTable)));
                    deleted++; inserted++;
                }
            }
            else if (hasOld)
            {
                changes.Add(MakeDelete(ob!, Loc(oldHeading, oOrd, ob!.IsTable)));
                deleted++;
            }
            else if (hasNew)
            {
                changes.Add(MakeInsert(nb!, Loc(newHeading, nOrd, nb!.IsTable)));
                inserted++;
            }

            // 제목 갱신 — 이후 블록들이 이 제목 아래로 묶이도록.
            if (ob is not null && IsHeading(ob)) oldHeading = Shorten(ob.Text);
            if (nb is not null && IsHeading(nb)) newHeading = Shorten(nb.Text);

            if (hasOld) oi++;
            if (hasNew) ni++;
        }

        return new StructureDiff(changes, new DiffSummary(inserted, deleted, modified, unchanged));
    }

    // ── 변경추적 통합본 ───────────────────────────────────────────────────
    // Compare 와 같은 블록 LCS 정렬을 쓰되, *미변경 블록까지 전부* 문서 순서대로 담아
    // 화면에서 전체를 읽으며 변경점을 따라갈 수 있게 한다. 수정 문단은 문자 단위 인라인 병합.
    public UnifiedDiff CompareUnified(DocStructure oldDoc, DocStructure newDoc, bool ignoreWhitespace = true)
    {
        var oldBlocks = oldDoc.Blocks;
        var newBlocks = newDoc.Blocks;

        var oldKeys = string.Join("\n", oldBlocks.Select(BlockKey));
        var newKeys = string.Join("\n", newBlocks.Select(BlockKey));
        var model = _builder.BuildDiffModel(oldKeys, newKeys, ignoreWhitespace);

        var oldLines = model.OldText.Lines;
        var newLines = model.NewText.Lines;
        int rowCount = Math.Max(oldLines.Count, newLines.Count);

        var items = new List<UnifiedItem>();
        int oi = 0, ni = 0;
        string heading = "";
        int inserted = 0, deleted = 0, modified = 0, unchanged = 0;

        for (int r = 0; r < rowCount; r++)
        {
            var oType = r < oldLines.Count ? oldLines[r].Type : ChangeType.Imaginary;
            var nType = r < newLines.Count ? newLines[r].Type : ChangeType.Imaginary;

            bool hasOld = oType != ChangeType.Imaginary && oi < oldBlocks.Count;
            bool hasNew = nType != ChangeType.Imaginary && ni < newBlocks.Count;

            DocBlock? ob = hasOld ? oldBlocks[oi] : null;
            DocBlock? nb = hasNew ? newBlocks[ni] : null;

            if (hasOld && hasNew)
            {
                if (oType == ChangeType.Unchanged && nType == ChangeType.Unchanged)
                {
                    items.Add(UnchangedItem(nb!, heading));
                    unchanged++;
                }
                else if (ob!.IsParagraph && nb!.IsParagraph)
                {
                    items.Add(ParaModItem(ob, nb, heading, ignoreWhitespace));
                    modified++;
                }
                else if (ob!.IsTable && nb!.IsTable)
                {
                    items.Add(TableModItem(ob, nb, heading));
                    modified++;
                }
                else
                {
                    items.Add(SoloItem(ob!, DiffChangeKind.Deleted, heading));
                    items.Add(SoloItem(nb!, DiffChangeKind.Inserted, heading));
                    deleted++; inserted++;
                }
            }
            else if (hasOld) { items.Add(SoloItem(ob!, DiffChangeKind.Deleted, heading)); deleted++; }
            else if (hasNew) { items.Add(SoloItem(nb!, DiffChangeKind.Inserted, heading)); inserted++; }

            if (ob is not null && IsHeading(ob)) heading = Shorten(ob.Text);
            if (nb is not null && IsHeading(nb)) heading = Shorten(nb.Text);

            if (hasOld) oi++;
            if (hasNew) ni++;
        }

        int? oldPages = oldDoc.Pages > 0 ? oldDoc.Pages : null;
        int? newPages = newDoc.Pages > 0 ? newDoc.Pages : null;
        return new UnifiedDiff(items, new DiffSummary(inserted, deleted, modified, unchanged), oldPages, newPages);
    }

    private UnifiedItem UnchangedItem(DocBlock b, string heading)
    {
        if (b.IsTable)
            return new UnifiedItem(UnifiedItemKind.Table, DiffChangeKind.Unchanged,
                Array.Empty<InlineRun>(), heading, null, Dims(b.Rows ?? new()), Dims(b.Rows ?? new()));
        return new UnifiedItem(UnifiedItemKind.Paragraph, DiffChangeKind.Unchanged,
            new[] { new InlineRun(Flatten(b.Text), DiffChangeKind.Unchanged) }, heading, null, null, null);
    }

    private UnifiedItem ParaModItem(DocBlock ob, DocBlock nb, string heading, bool ignoreWs)
    {
        var runs = _inline.InlineMerge(Flatten(ob.Text), Flatten(nb.Text), ignoreWs);
        return new UnifiedItem(UnifiedItemKind.Paragraph, DiffChangeKind.Modified, runs, heading, null, null, null);
    }

    private static UnifiedItem TableModItem(DocBlock ob, DocBlock nb, string heading)
    {
        var oldRows = ob.Rows ?? new();
        var newRows = nb.Rows ?? new();
        return new UnifiedItem(UnifiedItemKind.Table, DiffChangeKind.Modified,
            Array.Empty<InlineRun>(), heading, CellDiffs(oldRows, newRows), Dims(oldRows), Dims(newRows));
    }

    // 추가/삭제된 블록 한 개(문단 또는 표 통째).
    private static UnifiedItem SoloItem(DocBlock b, DiffChangeKind kind, string heading)
    {
        if (b.IsTable)
            return new UnifiedItem(UnifiedItemKind.Table, kind,
                Array.Empty<InlineRun>(), heading, null,
                kind == DiffChangeKind.Deleted ? Dims(b.Rows ?? new()) : null,
                kind == DiffChangeKind.Inserted ? Dims(b.Rows ?? new()) : null);
        return new UnifiedItem(UnifiedItemKind.Paragraph, kind,
            new[] { new InlineRun(Flatten(b.Text), kind) }, heading, null, null, null);
    }

    // ─ 변경 항목 생성 ───────────────────────────────────────────────────

    private StructChange MakeParagraphMod(DocBlock ob, DocBlock nb, string loc, bool ignoreWs)
    {
        // 문단 내부 줄바꿈은 한 줄로 펴서 단어 단위 인라인 diff (전/후 각 1라인).
        var oneOld = Flatten(ob.Text);
        var oneNew = Flatten(nb.Text);
        var inl = _inline.CompareText(oneOld, oneNew, ignoreWs);

        var oldLine = inl.Left.FirstOrDefault(l => l.Kind != DiffChangeKind.Imaginary)
                      ?? new DiffLine(null, DiffChangeKind.Deleted, oneOld, Array.Empty<DiffInlinePiece>());
        var newLine = inl.Right.FirstOrDefault(l => l.Kind != DiffChangeKind.Imaginary)
                      ?? new DiffLine(null, DiffChangeKind.Inserted, oneNew, Array.Empty<DiffInlinePiece>());

        return new StructChange(StructChangeKind.ModifiedParagraph, loc,
            oldLine, newLine, oneOld, oneNew, null, null, null);
    }

    private static StructChange MakeTableMod(DocBlock ob, DocBlock nb, string loc)
    {
        var oldRows = ob.Rows ?? new();
        var newRows = nb.Rows ?? new();
        return new StructChange(StructChangeKind.ModifiedTable, loc,
            null, null, null, null, CellDiffs(oldRows, newRows), Dims(oldRows), Dims(newRows));
    }

    // 표 두 개의 셀 격자를 비교해 바뀐 셀만 추려낸다 (행/열 기준 정렬, 공백 정규화).
    private static List<CellChange> CellDiffs(List<List<string>> oldRows, List<List<string>> newRows)
    {
        int rows = Math.Max(oldRows.Count, newRows.Count);
        var cells = new List<CellChange>();
        for (int r = 0; r < rows; r++)
        {
            var oRow = r < oldRows.Count ? oldRows[r] : new List<string>();
            var nRow = r < newRows.Count ? newRows[r] : new List<string>();
            int cols = Math.Max(oRow.Count, nRow.Count);
            for (int c = 0; c < cols; c++)
            {
                var o = c < oRow.Count ? oRow[c] : "";
                var n = c < nRow.Count ? nRow[c] : "";
                if (!CellEquals(o, n))
                    cells.Add(new CellChange(r, c, o, n));
            }
        }
        return cells;
    }

    private static StructChange MakeInsert(DocBlock b, string loc)
        => new(StructChangeKind.Inserted, loc, null, null, null, Preview(b), null,
               null, b.IsTable ? Dims(b.Rows ?? new()) : null);

    private static StructChange MakeDelete(DocBlock b, string loc)
        => new(StructChangeKind.Deleted, loc, null, null, Preview(b), null, null,
               b.IsTable ? Dims(b.Rows ?? new()) : null, null);

    // ─ 보조 ─────────────────────────────────────────────────────────────

    private static string BlockKey(DocBlock b)
        => b.IsTable
            ? "TBL" + Flatten(string.Join("", (b.Rows ?? new()).SelectMany(r => r)))
            : Flatten(b.Text);

    private static string Flatten(string s)
        => s.Replace('\r', ' ').Replace('\n', ' ');

    private static bool IsHeading(DocBlock b)
        => b.IsParagraph && (b.Level > 0 || HeadingRe.IsMatch(b.Text));

    private static string Shorten(string s)
    {
        s = Flatten(s).Trim();
        return s.Length <= 40 ? s : s[..40] + "…";
    }

    private static string Loc(string heading, int paraOrdinal, bool isTable)
    {
        var head = heading.Length > 0 ? heading + " › " : "";
        if (isTable) return head + (paraOrdinal > 0 ? $"표(문단 {paraOrdinal} 부근)" : "표");
        return head + (paraOrdinal > 0 ? $"문단 {paraOrdinal}" : "문단");
    }

    private static string Preview(DocBlock b)
    {
        if (b.IsTable)
        {
            var dims = Dims(b.Rows ?? new());
            var firstRow = (b.Rows ?? new()).FirstOrDefault();
            var head = firstRow is null ? "" : " " + Flatten(string.Join(" | ", firstRow));
            return $"[표 {dims}]{head}".Trim();
        }
        return Flatten(b.Text);
    }

    private static TableDims Dims(List<List<string>> rows)
        => new(rows.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count));

    private static bool CellEquals(string a, string b)
        => NormalizeCell(a) == NormalizeCell(b);

    private static readonly Regex WsRe = new(@"\s+", RegexOptions.Compiled);
    private static string NormalizeCell(string s) => WsRe.Replace(s, " ").Trim();
}
