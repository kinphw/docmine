// 구조 diff 결과 모델 — 변경 "목록"(리포트) 형태.
//
// 평문 좌우대조(SideBySideDiff)와 달리, 구조 diff 는 의미 단위(문단/표)의 변경을
// 위치 정보와 함께 나열한다. "제2조 적용범위 › 문단 5 가 이렇게 바뀜", "표 2행2열 변경".

namespace DocMine.Core.Diff;

public enum StructChangeKind
{
    Inserted,            // 새 문단/표 추가
    Deleted,             // 문단/표 삭제
    ModifiedParagraph,   // 문단 내용 변경 (단어 단위 인라인 diff 포함)
    ModifiedTable,       // 표 셀 변경
}

/// <summary>표의 한 셀 변경.</summary>
public sealed record CellChange(int Row, int Col, string Old, string New);

public sealed record StructChange(
    StructChangeKind Kind,
    string Location,            // "제2조 적용범위 › 문단 5" 등
    DiffLine? OldLine,          // ModifiedParagraph: 인라인 diff 적용된 전/후 라인
    DiffLine? NewLine,
    string? OldText,            // Inserted/Deleted: 원문 미리보기
    string? NewText,
    IReadOnlyList<CellChange>? Cells,   // ModifiedTable
    TableDims? OldDims,
    TableDims? NewDims);

public sealed record TableDims(int Rows, int Cols)
{
    public override string ToString() => $"{Rows}×{Cols}";
}

public sealed record StructureDiff(
    IReadOnlyList<StructChange> Changes,
    DiffSummary Summary);
