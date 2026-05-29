// PdfPig (순수 매니지드, Apache 2.0) 기반 PDF 텍스트 추출.
//
// 텍스트 추출 전략:
//   page.GetWords() 로 단어 + 좌표(BoundingBox)를 받아 reading order 재구성.
//   PDF 좌표는 원점 좌하단·Y 위로 증가 → 줄은 Bottom 내림차순, 단어는 Left 오름차순.
//   Python pdf_parser._extract_page_blocks 의도, iText LocationTextExtractionStrategy 와 동등.
//
// 라이선스: Apache 2.0 — 배포 의무 없음. iText(AGPL)와 A/B 비교 후 결정.

using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DocMine.Core.Pdf;

internal sealed class PdfPigPdfExtractor : IPdfTextExtractor
{
    public string Name => "PdfPig";

    public string Extract(string filePath)
    {
        var fp = PdfTextExtractor.WinLongPath(filePath);

        var sb = new StringBuilder();
        // 손상 PDF 에 관대하게 — 일부 오류는 건너뛰고 가능한 만큼 추출.
        var options = new ParsingOptions { UseLenientParsing = true };
        using var doc = PdfDocument.Open(fp, options);

        foreach (var page in doc.GetPages())
        {
            string pageText;
            try
            {
                pageText = ExtractPage(page);
            }
            catch
            {
                continue;  // 손상된 페이지는 skip — 다음 페이지 계속.
            }
            if (!string.IsNullOrEmpty(pageText))
            {
                sb.Append(pageText);
                sb.Append('\n');
            }
        }

        return PdfTextExtractor.Clean(sb.ToString());
    }

    // 단어를 줄 단위로 묶어 reading order 재구성.
    private static string ExtractPage(Page page)
    {
        var words = page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();
        if (words.Count == 0) return "";

        // Bottom 내림차순(위→아래), 같은 줄 내 Left 오름차순(좌→우).
        var ordered = words
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        var sb = new StringBuilder();
        var lineWords = new List<Word>();
        double lineBottom = 0;
        double lineTol = 0;

        void Flush()
        {
            if (lineWords.Count == 0) return;
            var first = true;
            foreach (var w in lineWords.OrderBy(x => x.BoundingBox.Left))
            {
                if (!first) sb.Append(' ');
                sb.Append(w.Text);
                first = false;
            }
            sb.Append('\n');
            lineWords.Clear();
        }

        foreach (var w in ordered)
        {
            var b = w.BoundingBox.Bottom;
            var h = Math.Abs(w.BoundingBox.Height);
            if (lineWords.Count == 0)
            {
                lineBottom = b;
                lineTol = Math.Max(1.0, h * 0.6);  // 줄 높이의 60% 이내면 같은 줄
                lineWords.Add(w);
            }
            else if (lineBottom - b <= lineTol)    // ordered 가 내림차순이라 b <= lineBottom
            {
                lineWords.Add(w);
            }
            else
            {
                Flush();
                lineBottom = b;
                lineTol = Math.Max(1.0, h * 0.6);
                lineWords.Add(w);
            }
        }
        Flush();

        return sb.ToString();
    }
}
