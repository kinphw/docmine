// PDF 본문 텍스트 추출기 추상화 — 엔진(PdfPig / iText)을 런타임에 교체하기 위한 계약.
//
// 두 구현(ITextPdfExtractor, PdfPigPdfExtractor)이 바이너리에 동시 포함되고,
// ⑤ 설정 탭의 선택값(AppConfig.PdfEngine)에 따라 PdfTextExtractor.Create 가 고른다.
// 성능/품질을 A/B 비교한 뒤 하나로 확정하기 위한 구조.

namespace DocMine.Core.Pdf;

public interface IPdfTextExtractor
{
    /// <summary>엔진 표시명 ("PdfPig" | "iText") — 로그/진단용.</summary>
    string Name { get; }

    /// <summary>PDF 파일에서 본문 텍스트 추출. 본문 없으면 빈 문자열.</summary>
    string Extract(string filePath);
}
