// DocumentComparer — 두 문서의 평문을 라인 단위로 비교.
//
// DiffPlex(SideBySideDiffBuilder) 가 라인 diff + 수정 라인의 단어 단위 subpiece 까지
// 만들어 준다. 여기서는 그 결과를 DocMine 도메인 모델(SideBySideDiff) 로 옮기고
// 통계를 집계한다. DiffPlex 타입은 이 클래스 밖으로 새어 나가지 않는다.

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

    /// <param name="ignoreWhitespace">공백만 다른 줄을 동일로 취급(노이즈 감소). 기본 true.</param>
    public SideBySideDiff CompareText(string oldText, string newText, bool ignoreWhitespace = true)
    {
        var model = _builder.BuildDiffModel(oldText ?? "", newText ?? "", ignoreWhitespace);

        var left  = model.OldText.Lines.Select(MapLine).ToList();
        var right = model.NewText.Lines.Select(MapLine).ToList();
        return new SideBySideDiff(left, right, BuildSummary(model));
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
