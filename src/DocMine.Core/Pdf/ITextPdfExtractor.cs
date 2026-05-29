// iText 9 (구 iText 7 계보, 매니지드 .NET) 기반 PDF 텍스트 추출.
//
// 텍스트 추출 전략:
//   LocationTextExtractionStrategy — iText 내장 좌표 기반 reading order 재구성.
//   Python pdf_parser._extract_page_blocks 의도와 동등.
//
// 라이선스: AGPL — 배포 시 의무 발생. PdfPig(Apache 2.0)와 A/B 비교 후 결정.

using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using ITextEngine = iText.Kernel.Pdf.Canvas.Parser.PdfTextExtractor;

namespace DocMine.Core.Pdf;

internal sealed class ITextPdfExtractor : IPdfTextExtractor
{
    public string Name => "iText";

    public string Extract(string filePath)
    {
        var fp = PdfTextExtractor.WinLongPath(filePath);

        var sb = new StringBuilder();
        // PdfReader/PdfDocument 가 IDisposable. using 으로 stream/resource 정리.
        using var reader = new PdfReader(fp);
        using var pdf = new PdfDocument(reader);

        var pageCount = pdf.GetNumberOfPages();
        for (int i = 1; i <= pageCount; i++)   // iText 는 1-based page index
        {
            string pageText;
            try
            {
                // 새 인스턴스 매 페이지마다 — strategy 가 stateful (누적 방지).
                var strategy = new LocationTextExtractionStrategy();
                pageText = ITextEngine.GetTextFromPage(pdf.GetPage(i), strategy);
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
}
