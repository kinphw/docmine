// 비교 결과를 HWP(X) 문서로 추출하기 위한 "스타일 런" 모델.
//
// 메인 프로세스가 diff 결과를 색상 입힌 런 목록(ReportDoc)으로 평탄화해 워커로 보내고,
// 워커가 한/글 COM 으로 FileNew → 런마다 글자색/굵기 지정 후 InsertText → SaveAs 한다.
// (서식 적용은 워커에서만 — 색상/위치 정보만 STDIO JSON 으로 전달.)

namespace DocMine.Core.Diff;

/// <summary>글자색·굵기·취소선·밑줄이 지정된 텍스트 한 조각. Breaks 만큼 뒤에 문단 나눔.</summary>
public sealed class ReportRun
{
    public string Text      { get; set; } = "";
    public int    R         { get; set; }
    public int    G         { get; set; }
    public int    B         { get; set; }
    public bool   Bold      { get; set; }
    public bool   Strike    { get; set; }   // 취소선 (변경추적: 삭제)
    public bool   Underline { get; set; }   // 밑줄   (변경추적: 추가)
    public int    Breaks    { get; set; }   // 이 런 뒤에 넣을 문단 나눔(BreakPara) 수
}

public sealed class ReportDoc
{
    public List<ReportRun> Runs { get; set; } = new();
}
