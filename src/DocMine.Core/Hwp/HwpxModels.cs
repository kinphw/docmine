// HWPX 도메인 모델 — Python hwp_parser.py 의 TextRun/Paragraph/TableCell/TableRow/Table/Section/HWPXDocument 1:1 포팅.
//
// 텍스트 추출만이 목적이라 서식 정보(폰트/색상 등) 는 보존 안 함 — 박건영님 운영 환경에서 검색 본문만 필요.

using System.Text;
using System.Text.RegularExpressions;

namespace DocMine.Core.Hwp;

public sealed record TextRun(string Text)
{
    public override string ToString() => Text;
}

public sealed class Paragraph
{
    public List<TextRun> Runs { get; set; } = new();
    public string? StyleId { get; set; }
    public int Level { get; set; }

    public string Text => string.Concat(Runs.Select(r => r.Text));
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
    public override string ToString() => Text;
}

public sealed class TableCell
{
    public List<Paragraph> Paragraphs { get; set; } = new();
    public int RowSpan { get; set; } = 1;
    public int ColSpan { get; set; } = 1;

    public string Text => string.Join("\n", Paragraphs.Select(p => p.Text));
}

public sealed class TableRow
{
    public List<TableCell> Cells { get; set; } = new();
    public string Text => string.Join("\t", Cells.Select(c => c.Text));
}

public sealed class Table
{
    public List<TableRow> Rows { get; set; } = new();
    public string Text => string.Join("\n", Rows.Select(r => r.Text));

    /// <summary>표를 plain text 로 — 셀 사이 ' | ', 행 사이 줄바꿈.</summary>
    public string ToPlainText(string cellSep = " | ", string rowSep = "\n")
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Rows.Count; i++)
        {
            if (i > 0) sb.Append(rowSep);
            for (int j = 0; j < Rows[i].Cells.Count; j++)
            {
                if (j > 0) sb.Append(cellSep);
                sb.Append(Rows[i].Cells[j].Text.Replace("\n", " "));
            }
        }
        return sb.ToString();
    }
}

/// <summary>문서 블록 — Paragraph 또는 Table.</summary>
public abstract class Block { }
public sealed class ParagraphBlock : Block { public Paragraph Paragraph; public ParagraphBlock(Paragraph p) { Paragraph = p; } }
public sealed class TableBlock     : Block { public Table     Table;     public TableBlock(Table t)     { Table = t; } }

public sealed class Section
{
    public int Index { get; set; }
    public List<Block> Blocks { get; set; } = new();

    public IEnumerable<Paragraph> Paragraphs => Blocks.OfType<ParagraphBlock>().Select(b => b.Paragraph);
    public IEnumerable<Table>     Tables     => Blocks.OfType<TableBlock>().Select(b => b.Table);

    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Blocks.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                switch (Blocks[i])
                {
                    case ParagraphBlock p: sb.Append(p.Paragraph.Text); break;
                    case TableBlock t:     sb.Append(t.Table.ToPlainText()); break;
                }
            }
            return sb.ToString();
        }
    }
}

public sealed class TextExtractionOptions
{
    public bool   PreserveBlankLines  { get; set; } = true;
    public bool   NormalizeWhitespace { get; set; } = true;
    public bool   StripLines          { get; set; } = true;
    public bool   IncludeTables       { get; set; } = true;
    public string SectionSeparator    { get; set; } = "\n\n";
    public string ParagraphSeparator  { get; set; } = "\n";
}

public sealed class HwpxDocument
{
    public string Path { get; set; } = "";
    public List<Section> Sections { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>섹션 전체를 합친 순수 텍스트 (기본 옵션).</summary>
    public string Text => ExtractText(new TextExtractionOptions());

    /// <summary>
    /// 텍스트 추출 — 옵션에 따라 빈 줄/공백 처리, 표 포함 여부 등 조정.
    /// Python HWPXDocument.extract_text 등가.
    /// </summary>
    public string ExtractText(TextExtractionOptions? options = null, bool? skipEmpty = null)
    {
        var opt = options ?? new TextExtractionOptions();
        var preserveBlanks = skipEmpty is not null ? !skipEmpty.Value : opt.PreserveBlankLines;

        var sectionTexts = new List<string>();
        foreach (var section in Sections)
        {
            var lines = new List<string>();
            foreach (var block in section.Blocks)
            {
                switch (block)
                {
                    case ParagraphBlock p: lines.Add(p.Paragraph.Text); break;
                    case TableBlock t when opt.IncludeTables: lines.Add(t.Table.ToPlainText()); break;
                }
            }
            var raw = string.Join(opt.ParagraphSeparator, lines);
            var processed = PostprocessLines(raw.Split('\n'),
                preserveBlanks, opt.NormalizeWhitespace, opt.StripLines);
            sectionTexts.Add(processed);
        }

        return string.Join(opt.SectionSeparator, sectionTexts.Where(t => t.Length > 0));
    }

    private static readonly Regex WsRe = new(@"[ \t\r\f\v]+", RegexOptions.Compiled);

    private static string PostprocessLines(
        IEnumerable<string> lines,
        bool preserveBlankLines,
        bool normalizeWhitespace,
        bool stripLines)
    {
        var processed = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw;
            if (normalizeWhitespace) line = WsRe.Replace(line, " ");
            if (stripLines)          line = line.Trim();
            if (line.Length > 0)     processed.Add(line);
            else if (preserveBlankLines) processed.Add("");
        }

        var text = string.Join("\n", processed);
        text = preserveBlankLines
            ? Regex.Replace(text, @"\n{3,}", "\n\n")  // 연속 빈 줄 최대 2개
            : Regex.Replace(text, @"\n{2,}", "\n");   // 빈 줄 전부 제거
        return text.Trim();
    }
}
