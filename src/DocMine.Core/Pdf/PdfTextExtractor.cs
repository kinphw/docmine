// PDF → 텍스트 추출 — iText 9 (구 iText 7 계보, 매니지드 .NET).
//
// 채택 배경:
//   PdfPig 가 운영 환경 한컴 export 큰 PDF 에서 silent crash.
//   MuPDFCore (PyMuPDF 동엔진) 가 호환성/안정성 OK 였으나 zip 29MB 부담.
//   iText = 자바 20년+ 검증 + .NET port + 매니지드 → 용량 ↓ + 안정성 ↑ 시도.
//
// 텍스트 추출 전략:
//   LocationTextExtractionStrategy — iText 의 내장 좌표 기반 reading order
//   재구성. PdfPig 의 GetWords() + 좌표 sort 와 같은 모델, iText 가 자체 처리.
//   Python pdf_parser.py 의 _extract_page_blocks 의도와 동등.
//
// 라이선스: AGPL — 박건영님 개인 사용 전제. 배포 시 의무 발생.
//
// DRM 호환 long-path:
//   - Windows MAX_PATH(260자) 미만 경로는 그대로 — \\?\ prefix 가 DRM 후킹을
//     비활성화시켜 암호화된 byte 가 그대로 흘러올 위험.

using System.Text;
using System.Text.RegularExpressions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using ITextExtractor = iText.Kernel.Pdf.Canvas.Parser.PdfTextExtractor;

namespace DocMine.Core.Pdf;

public static class PdfTextExtractor
{
    private const int WinMaxPath = 260;

    // C0 제어문자(탭·줄바꿈 제외) 제거.
    private static readonly Regex CtrlRe = new(@"[\x00-\x08\x0b\x0c\x0e-\x1f]", RegexOptions.Compiled);

    /// <summary>PDF 파일에서 본문 텍스트 추출.</summary>
    public static string Extract(string filePath)
    {
        var fp = WinLongPath(filePath);

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
                // LocationTextExtractionStrategy 가 좌표 sort 자체 처리.
                // 새 인스턴스 매 페이지마다 — strategy 가 stateful (누적 방지).
                var strategy = new LocationTextExtractionStrategy();
                pageText = ITextExtractor.GetTextFromPage(pdf.GetPage(i), strategy);
            }
            catch
            {
                // 손상된 페이지는 skip — 다음 페이지 계속.
                continue;
            }
            if (!string.IsNullOrEmpty(pageText))
            {
                sb.Append(pageText);
                sb.Append('\n');
            }
        }

        return Clean(sb.ToString());
    }

    private static string Clean(string text)
    {
        text = text.Replace("\xa0", " ");
        text = CtrlRe.Replace(text, "");
        return text.Trim();
    }

    public static string WinLongPath(string path)
    {
        var p = Path.GetFullPath(path);
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) return p;
        if (p.Length < WinMaxPath) return p;
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + p[2..];
        return @"\\?\" + p;
    }
}
