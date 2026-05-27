// PDF → 텍스트 추출 — MuPDFCore (PyMuPDF 와 동엔진).
//
// 운영 환경 PdfPig silent crash 재현 (28MB Hancom PDF 등 GetWords() OOM
// #820 패턴) → MuPDF 의 native parser + lazy 페이지 로드로 전환.
// Python pdf_parser.py 의 _extract_page_blocks 와 거의 1:1:
//   - GetStructuredTextPage(i) 가 PyMuPDF page.get_text("blocks") 등가
//   - Block 단위 (Y0, X0) 좌표 정렬 후 line text 합쳐 reading order 재구성
//
// 좌표계: MuPDFCore 의 BoundingBox 는 top-left 원점 (PyMuPDF 와 동일).
//
// Thread/process safety:
//   MuPDFContext 는 thread-unsafe. PdfWorker 프로세스 격리로 해결 — 각 워커
//   프로세스가 자기 SharedContext 1개. 워커 안에서는 단일 thread 호출.
//   MuPDFDocument 생성자 throw 시 partial-constructed 객체의 Finalize 가 ctx
//   를 못 찾는 LifetimeManagementException 회피 위해 ctx 는 process 수명
//   동안 dispose 하지 않음.
//
// DRM 호환 long-path:
//   - Windows MAX_PATH(260자) 미만 경로는 그대로 — \\?\ prefix 가 DRM 후킹을
//     비활성화시켜 암호화된 byte 가 그대로 흘러올 위험.

using System.Text;
using System.Text.RegularExpressions;
using MuPDFCore;

namespace DocMine.Core.Pdf;

public static class PdfTextExtractor
{
    private const int WinMaxPath = 260;

    // C0 제어문자(탭·줄바꿈 제외) 제거.
    private static readonly Regex CtrlRe = new(@"[\x00-\x08\x0b\x0c\x0e-\x1f]", RegexOptions.Compiled);

    // MuPDFContext 는 워커 process 수명 동안 절대 dispose 안 함 — LifetimeManagement 회피.
    private static readonly Lazy<MuPDFContext> SharedContext = new(() => new MuPDFContext());

    /// <summary>PDF 파일에서 본문 텍스트 추출.</summary>
    public static string Extract(string filePath)
    {
        var fp = WinLongPath(filePath);

        var sb = new StringBuilder();
        using var doc = new MuPDFDocument(SharedContext.Value, fp);

        for (int i = 0; i < doc.Pages.Count; i++)
        {
            string pageText;
            try
            {
                pageText = ExtractPageByBlocks(doc, i);
            }
            catch
            {
                // 손상된 페이지는 skip.
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

    private static string ExtractPageByBlocks(MuPDFDocument doc, int pageIndex)
    {
        using var stp = doc.GetStructuredTextPage(pageIndex);

        // 각 block 의 plain text + (Y0, X0) 좌표 수집.
        var items = new List<(double Y, double X, string Text)>();
        foreach (var block in stp)
        {
            // 같은 block 의 line 들은 \n 으로 join — PyMuPDF blocks 의 b[4] 와 동일.
            var lineSb = new StringBuilder();
            for (int j = 0; j < block.Count; j++)
            {
                if (j > 0) lineSb.Append('\n');
                lineSb.Append(block[j].Text);
            }
            var text = lineSb.ToString();
            if (string.IsNullOrEmpty(text)) continue;

            // y/x 약간 양자화 (소수점 1자리) — 부동소수 미세 차이로 줄/열 어긋남 회피.
            var y = Math.Round(block.BoundingBox.Y0, 1);
            var x = Math.Round(block.BoundingBox.X0, 1);
            items.Add((y, x, text));
        }

        // (Y, X) 정렬 — 위에서 아래·왼쪽에서 오른쪽.
        items.Sort((a, b) =>
        {
            var c = a.Y.CompareTo(b.Y);
            return c != 0 ? c : a.X.CompareTo(b.X);
        });

        var sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(items[i].Text);
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
