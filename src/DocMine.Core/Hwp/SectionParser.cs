// SectionParser — HWPX section XML 을 Section 객체로 변환.
// Python hwp_parser.py 의 ParagraphParser / TableParser / SectionParser 1:1 포팅.
//
// 핵심 패턴: 네임스페이스 무관 로컬 이름 매칭 (한컴 + GPT/기타 생성기 변형 모두 흡수).
// 1차/2차/3차 fallback — para 구조가 변형돼도 텍스트를 최대한 긁어옴.

using System.Xml.Linq;

namespace DocMine.Core.Hwp;

public abstract class BaseNodeParser
{
    public abstract bool CanParse(string localTag);
    public abstract Block? Parse(XElement element);
}

internal static class XmlHelpers
{
    /// <summary>네임스페이스 무관 로컬 이름.</summary>
    public static string Local(XName name) => name.LocalName;

    /// <summary>로컬 이름이 target 중 하나인 자손 요소 (재귀).</summary>
    public static IEnumerable<XElement> FindAllByLocal(XElement element, params string[] targets)
    {
        var set = new HashSet<string>(targets, StringComparer.Ordinal);
        foreach (var child in element.Elements())
        {
            if (set.Contains(Local(child.Name))) yield return child;
            foreach (var grand in FindAllByLocal(child, targets)) yield return grand;
        }
    }

    /// <summary>로컬 이름이 target 중 하나인 직계 자식.</summary>
    public static IEnumerable<XElement> IterDirectByLocal(XElement element, params string[] targets)
    {
        var set = new HashSet<string>(targets, StringComparer.Ordinal);
        foreach (var child in element.Elements())
            if (set.Contains(Local(child.Name))) yield return child;
    }
}

public sealed class ParagraphParser : BaseNodeParser
{
    private static readonly HashSet<string> ParaTags = new() { "para", "p", "PARA", "P" };
    private static readonly HashSet<string> TextTags = new() { "t", "T", "text" };

    public override bool CanParse(string localTag) => ParaTags.Contains(localTag);

    public override Block? Parse(XElement element)
    {
        var runs = new List<TextRun>();

        var styleId = element.Attribute("styleIDRef")?.Value ?? element.Attribute("styleId")?.Value;
        var level = 0;
        var levelStr = element.Attribute("outlineLevel")?.Value;
        if (levelStr is not null && int.TryParse(levelStr, out var lv)) level = lv;

        // ── 1차: <run> 하위의 <t> ────────────────────────────────────
        foreach (var runEl in XmlHelpers.FindAllByLocal(element, "run", "Run", "RUN"))
        {
            var parts = new List<string>();
            foreach (var tEl in XmlHelpers.IterDirectByLocal(runEl, "t", "T"))
            {
                parts.Add(tEl.Value ?? "");
                // XDocument 의 tail 등가물 — LINQ-to-XML 에는 명시적 tail 개념 없음.
                // 단, <t>foo</t>tail<br/> 같은 case 는 t 의 NextNode 가 XText 면 그게 tail.
                if (tEl.NextNode is XText tailText && !string.IsNullOrWhiteSpace(tailText.Value))
                    parts.Add(tailText.Value);
            }
            foreach (var _ in XmlHelpers.IterDirectByLocal(runEl, "lineBreak", "LineBreak"))
                parts.Add("\n");
            if (parts.Count > 0)
                runs.Add(new TextRun(string.Concat(parts)));
        }

        // ── 2차 fallback: <run> 없이 직계 <t> ────────────────────────
        if (runs.Count == 0)
        {
            var direct = string.Concat(
                XmlHelpers.IterDirectByLocal(element, "t", "T").Select(t => t.Value ?? ""));
            if (direct.Length > 0) runs.Add(new TextRun(direct));
        }

        // ── 3차 fallback: 전체 descendant 텍스트 긁기 ─────────────────
        if (runs.Count == 0)
        {
            var chunks = new List<string>();
            foreach (var node in element.DescendantsAndSelf())
            {
                var local = XmlHelpers.Local(node.Name);
                if (TextTags.Contains(local) && !string.IsNullOrEmpty(node.Value))
                    chunks.Add(node.Value);
                if (node != element && node.NextNode is XText tail && !string.IsNullOrWhiteSpace(tail.Value))
                    chunks.Add(tail.Value);
            }
            if (chunks.Count > 0) runs.Add(new TextRun(string.Concat(chunks)));
        }

        return new ParagraphBlock(new Paragraph { Runs = runs, StyleId = styleId, Level = level });
    }
}

public sealed class TableParser : BaseNodeParser
{
    private static readonly HashSet<string> TblTags = new() { "tbl", "table", "Tbl", "Table", "TBL", "TABLE" };
    private readonly ParagraphParser _paraParser = new();

    public override bool CanParse(string localTag) => TblTags.Contains(localTag);

    public override Block? Parse(XElement element)
    {
        var rows = new List<TableRow>();
        foreach (var trEl in XmlHelpers.FindAllByLocal(element, "tr", "Tr", "TR"))
        {
            var cells = new List<TableCell>();
            foreach (var tcEl in XmlHelpers.IterDirectByLocal(trEl, "tc", "Tc", "TC"))
            {
                var rowSpan = int.TryParse(tcEl.Attribute("rowSpan")?.Value, out var rs) ? rs : 1;
                var colSpan = int.TryParse(tcEl.Attribute("colSpan")?.Value, out var cs) ? cs : 1;
                if (rowSpan < 1) rowSpan = 1;
                if (colSpan < 1) colSpan = 1;
                var paragraphs = new List<Paragraph>();
                foreach (var paraEl in XmlHelpers.FindAllByLocal(tcEl, "para", "p", "PARA", "P"))
                {
                    if (_paraParser.Parse(paraEl) is ParagraphBlock pb) paragraphs.Add(pb.Paragraph);
                }
                cells.Add(new TableCell { Paragraphs = paragraphs, RowSpan = rowSpan, ColSpan = colSpan });
            }
            if (cells.Count > 0) rows.Add(new TableRow { Cells = cells });
        }
        return rows.Count > 0 ? new TableBlock(new Table { Rows = rows }) : null;
    }
}

public sealed class SectionParser
{
    private readonly List<BaseNodeParser> _parsers = new() { new ParagraphParser(), new TableParser() };

    public void Register(BaseNodeParser parser)        => _parsers.Add(parser);
    public void RegisterFirst(BaseNodeParser parser)   => _parsers.Insert(0, parser);

    public Section ParseElement(XElement element, int index)
    {
        var section = new Section { Index = index };
        CollectBlocks(element, section.Blocks);
        return section;
    }

    public Section ParseXml(byte[] xmlBytes, int index)
    {
        try
        {
            using var ms = new MemoryStream(xmlBytes);
            var doc = XDocument.Load(ms);
            return ParseElement(doc.Root!, index);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new HwpxParseError($"섹션 {index} XML 파싱 실패: {ex.Message}", ex);
        }
    }

    private void CollectBlocks(XElement element, List<Block> blocks)
    {
        foreach (var child in element.Elements())
        {
            var local = XmlHelpers.Local(child.Name);
            var block = Dispatch(local, child);
            if (block is not null)
            {
                blocks.Add(block);
            }
            else
            {
                // 알 수 없는 컨테이너 — 재귀 탐색. para/table 은 이미 처리됐으니 제외.
                if (local is not ("para" or "p" or "tbl" or "table" or "PARA" or "P" or "TBL" or "TABLE"))
                    CollectBlocks(child, blocks);
            }
        }
    }

    private Block? Dispatch(string localTag, XElement element)
    {
        foreach (var p in _parsers)
            if (p.CanParse(localTag)) return p.Parse(element);
        return null;
    }
}
