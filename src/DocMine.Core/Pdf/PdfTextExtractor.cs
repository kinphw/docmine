// PDF → 텍스트 추출 — PdfPig 기반.
//
// Python pdf_parser.py 의 _extract_page_blocks 등가.
//
// 알고리즘 선택:
//   - 1차 시도였던 ContentOrderTextExtractor 는 내부 layout analysis(XY-cut 류)
//     를 돌려 reading order 를 정렬하는데, 페이지당 비용이 적지 않음.
//   - 현재는 page.GetWords() + (y, x) 좌표 sort 패턴 — Python 의 blocks 좌표
//     정렬과 동일 모델. 표/다단 레이아웃에서도 가독성 큰 손실 없이 더 빠름.
//
// DRM 호환 long-path:
//   - Windows MAX_PATH(260자) 미만 경로는 그대로 — \\?\ prefix 를 붙이면
//     DRM(Fasoo/MarkAny 등) 의 파일 I/O 후킹 계층이 발동하지 않아 암호화된
//     원본 바이트가 그대로 흘러갈 위험. 260자 이상만 prefix 적용.

using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DocMine.Core.Pdf;

public static class PdfTextExtractor
{
    private const int WinMaxPath = 260;

    // C0 제어문자(탭·줄바꿈 제외) 제거.
    private static readonly Regex CtrlRe = new(@"[\x00-\x08\x0b\x0c\x0e-\x1f]", RegexOptions.Compiled);

    // PdfPig 옵션 — 손상/누락된 부속 정보로 throw 하지 않도록 관대하게.
    private static readonly ParsingOptions OpenOpts = new()
    {
        UseLenientParsing = true,
    };

    /// <summary>PDF 파일에서 본문 텍스트 추출.</summary>
    public static string Extract(string filePath)
    {
        var fp = WinLongPath(filePath);

        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(fp, OpenOpts);
        foreach (var page in doc.GetPages())
        {
            string pageText;
            try
            {
                pageText = ExtractPageByWords(page);
            }
            catch
            {
                // 일부 손상 페이지는 throw 가능 — 해당 페이지만 skip.
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

    /// <summary>
    /// 페이지 단위 추출 — words 의 (y 양자화, x) sort 후 같은 y 그룹은 공백 join,
    /// y 그룹 바뀌면 줄바꿈. Python _extract_page_blocks 와 동일 의도.
    /// </summary>
    private static string ExtractPageByWords(Page page)
    {
        // PdfPig 좌표는 PDF 표준 — 페이지 하단이 y=0. 위에서 아래로 정렬하려면
        // y(Bottom) 가 큰 것부터.
        // y 좌표를 정수로 양자화 (반올림) 해서 같은 줄로 묶음 — 부동소수 미세
        // 차이로 줄이 어긋나는 현상 회피.
        var words = page.GetWords();
        if (words is null) return string.Empty;

        var ordered = words
            .Where(w => !string.IsNullOrEmpty(w.Text))
            .Select(w => (
                Word: w,
                YKey: -(long)Math.Round(w.BoundingBox.Bottom),  // 음수로: 큰 값(위) 먼저
                X:    w.BoundingBox.Left))
            .OrderBy(t => t.YKey)
            .ThenBy(t => t.X)
            .ToList();
        if (ordered.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        long? lastY = null;
        foreach (var t in ordered)
        {
            if (lastY is null)
            {
                sb.Append(t.Word.Text);
            }
            else if (t.YKey != lastY.Value)
            {
                sb.Append('\n');
                sb.Append(t.Word.Text);
            }
            else
            {
                sb.Append(' ');
                sb.Append(t.Word.Text);
            }
            lastY = t.YKey;
        }
        return sb.ToString();
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
