// DocStructure — 구조 diff(v2) 입력용 경량 DTO.
//
// HwpxDocument(Section→Block→Paragraph/Table) 을 비교에 필요한 만큼만 평탄화한 형태.
// 워커(자식 프로세스)가 HWPX 파싱 결과를 이 DTO 로 만들어 STDIO JSON 으로 돌려주고,
// 메인 프로세스의 DocumentStructureComparer 가 이를 받아 비교한다.
// (System.Text.Json 직렬화 대상 — get/set 자동 프로퍼티만 사용.)

using System.Text.Json.Serialization;
using DocMine.Core.Hwp;

namespace DocMine.Core.Diff;

public sealed class DocStructure
{
    public List<DocBlock> Blocks { get; set; } = new();

    /// <summary>
    /// HwpxDocument 를 평탄화. 공백뿐인 문단은 노이즈라 제외(텍스트 추출의 skipEmpty 와 같은 취지).
    /// 표는 항상 보존. 섹션 경계는 비교에 불필요해 무시하고 문서 순서대로 이어 붙인다.
    /// </summary>
    public static DocStructure FromHwpx(HwpxDocument doc)
    {
        var s = new DocStructure();
        foreach (var section in doc.Sections)
        {
            foreach (var block in section.Blocks)
            {
                switch (block)
                {
                    case ParagraphBlock pb:
                        if (string.IsNullOrWhiteSpace(pb.Paragraph.Text)) continue;
                        s.Blocks.Add(new DocBlock
                        {
                            Kind    = DocBlock.Paragraph,
                            Level   = pb.Paragraph.Level,
                            StyleId = pb.Paragraph.StyleId,
                            Text    = pb.Paragraph.Text,
                        });
                        break;

                    case TableBlock tb:
                        var rows = tb.Table.Rows
                            .Select(r => r.Cells.Select(c => c.Text).ToList())
                            .ToList();
                        s.Blocks.Add(new DocBlock { Kind = DocBlock.Table, Rows = rows });
                        break;
                }
            }
        }
        return s;
    }
}

public sealed class DocBlock
{
    public const string Paragraph = "p";
    public const string Table     = "table";

    public string  Kind    { get; set; } = Paragraph;   // "p" | "table"

    // 문단:
    public int     Level   { get; set; }
    public string? StyleId { get; set; }
    public string  Text    { get; set; } = "";

    // 표: 행-우선 셀 텍스트 격자.
    public List<List<string>>? Rows { get; set; }

    [JsonIgnore] public bool IsParagraph => Kind == Paragraph;
    [JsonIgnore] public bool IsTable      => Kind == Table;
}
