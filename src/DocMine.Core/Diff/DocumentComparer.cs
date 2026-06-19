// DocumentComparer — 두 문서의 평문을 라인 단위로 비교.
//
// DiffPlex(SideBySideDiffBuilder) 가 라인 diff + 수정 라인의 단어 단위 subpiece 까지
// 만들어 준다. 여기서는 그 결과를 DocMine 도메인 모델(SideBySideDiff) 로 옮기고
// 통계를 집계한다. DiffPlex 타입은 이 클래스 밖으로 새어 나가지 않는다.

using System.Text;
using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace DocMine.Core.Diff;

public sealed class DocumentComparer
{
    // 라인 단위로 정렬하되, 수정 라인의 인라인(라인 내부) 비교는 *문자 단위* 로 한다.
    // 한국어는 어절(공백 토큰)이 길어 단어 단위 하이라이트가 거칠다 — "전 직원"→"전 임직원"
    // 에서 단어 단위는 토큰 전체를, 문자 단위는 "임" 만 강조해 훨씬 정밀하다.
    // (kinphw diffinder 의 char-level LCS 접근을 DiffPlex CharacterChunker 로 반영.)
    private readonly SideBySideDiffBuilder _builder =
        new(new Differ(), LineChunker.Instance, CharacterChunker.Instance);

    // 변경추적 인라인 병합용 — 문자 단위 raw diff (좌우 분리 없이 한 흐름으로 병합).
    private readonly Differ _differ = new();

    /// <param name="ignoreWhitespace">공백만 다른 줄을 동일로 취급(노이즈 감소). 기본 true.</param>
    public SideBySideDiff CompareText(string oldText, string newText, bool ignoreWhitespace = true)
    {
        var model = _builder.BuildDiffModel(oldText ?? "", newText ?? "", ignoreWhitespace);

        var left  = model.OldText.Lines.Select(MapLine).ToList();
        var right = model.NewText.Lines.Select(MapLine).ToList();
        return new SideBySideDiff(left, right, BuildSummary(model));
    }

    /// <summary>
    /// 두 텍스트를 *문자 단위* 로 비교해 한 흐름의 변경추적 조각(동일/삭제/추가)으로 병합.
    /// "전 직원" → "전 임직원" 이면 [동일 "전 "][추가 "임"][동일 "직원"] 처럼 나온다.
    /// </summary>
    public IReadOnlyList<InlineRun> InlineMerge(string oldText, string newText, bool ignoreWhitespace = false)
    {
        var result = _differ.CreateDiffs(oldText ?? "", newText ?? "",
            ignoreWhitespace, ignoreCase: false, CharacterChunker.Instance);

        var runs = new List<InlineRun>();
        int bPos = 0;   // 소비한 new 조각 위치 (동일 구간은 new 기준으로 출력)

        foreach (var block in result.DiffBlocks)
        {
            if (block.InsertStartB > bPos)
                Add(runs, DiffChangeKind.Unchanged, Join(result.PiecesNew, bPos, block.InsertStartB - bPos));
            if (block.DeleteCountA > 0)
                Add(runs, DiffChangeKind.Deleted, Join(result.PiecesOld, block.DeleteStartA, block.DeleteCountA));
            if (block.InsertCountB > 0)
                Add(runs, DiffChangeKind.Inserted, Join(result.PiecesNew, block.InsertStartB, block.InsertCountB));
            bPos = block.InsertStartB + block.InsertCountB;
        }
        if (bPos < result.PiecesNew.Length)
            Add(runs, DiffChangeKind.Unchanged, Join(result.PiecesNew, bPos, result.PiecesNew.Length - bPos));

        return runs;

        static void Add(List<InlineRun> runs, DiffChangeKind kind, string text)
        {
            if (text.Length == 0) return;
            if (runs.Count > 0 && runs[^1].Kind == kind)
                runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
            else
                runs.Add(new InlineRun(text, kind));
        }
        static string Join(string[] pieces, int start, int count)
        {
            var sb = new StringBuilder(count);
            for (int i = 0; i < count; i++) sb.Append(pieces[start + i]);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 평문 텍스트를 라인 단위로 정렬해 변경추적 통합 모델로 — 구조 추출 실패 시 폴백.
    /// 미변경 줄도 모두 담아 전체 문서를 읽을 수 있게 한다.
    /// </summary>
    public UnifiedDiff CompareUnifiedText(string oldText, string newText, bool ignoreWhitespace = true)
    {
        var model = _builder.BuildDiffModel(oldText ?? "", newText ?? "", ignoreWhitespace);
        var left  = model.OldText.Lines;
        var right = model.NewText.Lines;
        int n = Math.Max(left.Count, right.Count);

        var items = new List<UnifiedItem>(n);
        int ins = 0, del = 0, mod = 0, unch = 0;

        for (int i = 0; i < n; i++)
        {
            var L = i < left.Count  ? left[i]  : null;
            var R = i < right.Count ? right[i] : null;
            switch (R?.Type)
            {
                case ChangeType.Unchanged:
                    items.Add(Para(DiffChangeKind.Unchanged, R.Text ?? "")); unch++; break;
                case ChangeType.Inserted:
                    items.Add(Para(DiffChangeKind.Inserted, R.Text ?? "")); ins++; break;
                case ChangeType.Modified:
                    items.Add(ParaRuns(InlineMerge(L?.Text ?? "", R.Text ?? "", ignoreWhitespace))); mod++; break;
                default: // Imaginary — 왼쪽 삭제 줄
                    if (L?.Type == ChangeType.Deleted) { items.Add(Para(DiffChangeKind.Deleted, L.Text ?? "")); del++; }
                    break;
            }
        }
        return new UnifiedDiff(items, new DiffSummary(ins, del, mod, unch));

        static UnifiedItem Para(DiffChangeKind kind, string text)
            => new(UnifiedItemKind.Paragraph, kind, new[] { new InlineRun(text, kind) }, null, null, null, null);
        static UnifiedItem ParaRuns(IReadOnlyList<InlineRun> runs)
            => new(UnifiedItemKind.Paragraph, DiffChangeKind.Modified, runs, null, null, null, null);
    }

    private static DiffLine MapLine(DiffPiece p)
    {
        IReadOnlyList<DiffInlinePiece> pieces =
            p.SubPieces is { Count: > 0 }
                ? p.SubPieces
                    .Select(sp => new DiffInlinePiece(sp.Text ?? "", Map(sp.Type)))
                    .ToList()
                : Array.Empty<DiffInlinePiece>();

        return new DiffLine(p.Position, Map(p.Type), p.Text ?? "", pieces);
    }

    private static DiffSummary BuildSummary(SideBySideDiffModel model)
    {
        int inserted = 0, modified = 0, unchanged = 0, deleted = 0;

        foreach (var l in model.NewText.Lines)
        {
            switch (l.Type)
            {
                case ChangeType.Inserted:  inserted++;  break;
                case ChangeType.Modified:  modified++;  break;
                case ChangeType.Unchanged: unchanged++; break;
            }
        }
        foreach (var l in model.OldText.Lines)
            if (l.Type == ChangeType.Deleted) deleted++;

        return new DiffSummary(inserted, deleted, modified, unchanged);
    }

    private static DiffChangeKind Map(ChangeType t) => t switch
    {
        ChangeType.Inserted  => DiffChangeKind.Inserted,
        ChangeType.Deleted   => DiffChangeKind.Deleted,
        ChangeType.Modified  => DiffChangeKind.Modified,
        ChangeType.Imaginary => DiffChangeKind.Imaginary,
        _                    => DiffChangeKind.Unchanged,
    };
}
