// 변경추적(track-changes) 통합 인라인 모델 — 변경 전/후를 한 흐름으로 병합해
// "삭제=취소선, 추가=밑줄" 처럼 워드/한글 변경내용추적과 동형으로 보여주기 위한 DTO.
//
// SideBySideDiff(좌우 분리)와 달리, 이 모델은 문서를 통째로(미변경 포함) 순서대로 담아
// 화면에서 파일을 열지 않고도 전체를 읽으며 변경점을 따라갈 수 있게 한다.

namespace DocMine.Core.Diff;

/// <summary>변경추적 인라인 한 조각. Unchanged/Deleted/Inserted 만 사용.</summary>
public sealed record InlineRun(string Text, DiffChangeKind Kind);

public enum UnifiedItemKind
{
    Paragraph,   // 문단 — Runs 에 인라인 병합 결과
    Table,       // 표 — Cells/Dims 사용 (Runs 비어있음)
}

/// <summary>
/// 변경추적 뷰의 한 항목(문단 또는 표). 문서 순서대로 전체를 담는다(미변경 포함).
/// LineKind 는 항목 전체 성격(Unchanged/Inserted/Deleted/Modified).
/// </summary>
public sealed record UnifiedItem(
    UnifiedItemKind Kind,
    DiffChangeKind LineKind,
    IReadOnlyList<InlineRun> Runs,
    string? Location,
    IReadOnlyList<CellChange>? Cells,
    TableDims? OldDims,
    TableDims? NewDims);

public sealed record UnifiedDiff(
    IReadOnlyList<UnifiedItem> Items,
    DiffSummary Summary,
    int? OldPages = null,
    int? NewPages = null);
