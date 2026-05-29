// PDF → 텍스트 추출 — 엔진 선택 파사드 + 공용 유틸.
//
// 실제 추출은 IPdfTextExtractor 구현이 담당:
//   - ITextPdfExtractor   (iText 9)
//   - PdfPigPdfExtractor  (PdfPig)
// 둘 다 바이너리에 포함되고, AppConfig.PdfEngine 설정으로 런타임 선택 (성능 비교용).
//
// 이 클래스는 (a) 엔진 팩토리 Create, (b) 엔진과 무관한 공용 유틸(WinLongPath/Clean)을 제공.
//
// DRM 호환 long-path:
//   - Windows MAX_PATH(260자) 미만 경로는 그대로 — \\?\ prefix 가 DRM 후킹을
//     비활성화시켜 암호화된 byte 가 그대로 흘러올 위험.

using System.Text.RegularExpressions;

namespace DocMine.Core.Pdf;

public static class PdfTextExtractor
{
    /// <summary>설정값이 비었거나 알 수 없을 때의 기본 엔진.</summary>
    public const string DefaultEngine = "iText";

    private const int WinMaxPath = 260;

    // C0 제어문자(탭·줄바꿈 제외) 제거. 두 엔진 공용.
    internal static readonly Regex CtrlRe = new(@"[\x00-\x08\x0b\x0c\x0e-\x1f]", RegexOptions.Compiled);

    /// <summary>엔진명("PdfPig"/"iText", 대소문자·공백 무시)으로 추출기 인스턴스 생성.</summary>
    public static IPdfTextExtractor Create(string? engineName)
        => (engineName ?? "").Trim().ToLowerInvariant() switch
        {
            "pdfpig" => new PdfPigPdfExtractor(),
            "itext"  => new ITextPdfExtractor(),
            _         => new ITextPdfExtractor(),   // 기본
        };

    /// <summary>하위 호환 — 엔진 미지정 시 기본 엔진으로 1회성 추출.</summary>
    public static string Extract(string filePath) => Create(DefaultEngine).Extract(filePath);

    /// <summary>제어문자 제거 + NBSP 정규화 + trim. 두 엔진 공용.</summary>
    internal static string Clean(string text)
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
