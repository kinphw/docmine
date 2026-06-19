// 문서 비교 결과 도메인 모델 — DiffPlex 타입을 UI 에 직접 노출하지 않기 위한 래퍼.
//
// v1 은 평문 라인 diff 만 담지만, v2 구조 diff(Paragraph/Table 정렬) 도 같은
// SideBySideDiff 모델로 결과를 돌려주도록 설계 — 렌더러(SideBySideDiffView) 재사용.

namespace DocMine.Core.Diff;

/// <summary>라인/조각의 변경 종류. DiffPlex ChangeType 의 도메인 등가.</summary>
public enum DiffChangeKind
{
    Unchanged,
    Inserted,
    Deleted,
    Modified,
    /// <summary>반대편 삽입/삭제에 맞춰 끼워 넣은 빈 줄(정렬용 패딩).</summary>
    Imaginary,
}

/// <summary>수정된 라인 안의 단어 단위 조각 — 인라인 하이라이트용.</summary>
public sealed record DiffInlinePiece(string Text, DiffChangeKind Kind);

/// <summary>한 쪽(변경 전 또는 후) 패널의 한 줄.</summary>
public sealed record DiffLine(
    int? Position,
    DiffChangeKind Kind,
    string Text,
    IReadOnlyList<DiffInlinePiece> Pieces);

/// <summary>변경 통계. Modified 는 양쪽에 한 번씩만 집계되도록 new 패널 기준으로 셈.</summary>
public sealed record DiffSummary(int Inserted, int Deleted, int Modified, int Unchanged)
{
    public int ChangedLines => Inserted + Deleted + Modified;
    public bool HasChanges  => ChangedLines > 0;
}

/// <summary>
/// 좌우 대조 diff. Left/Right 는 패딩(Imaginary) 으로 같은 길이가 보장돼
/// i 번째 줄끼리 짝을 이룸 — 렌더러가 행 정렬을 그대로 신뢰할 수 있음.
/// </summary>
public sealed record SideBySideDiff(
    IReadOnlyList<DiffLine> Left,
    IReadOnlyList<DiffLine> Right,
    DiffSummary Summary);
