// 스캔 → 적재 핸드오프용 레지스트리.
//
// ① 스캔 탭이 성공적으로 CSV 를 쓰면 그 경로를 여기 publish.
// ② HWP/PDF 적재 탭이 자기 탭으로 전환될 때 (VisibleChanged) 이 값을 조회해
//    "↻ 최근 스캔: <파일명>" 버튼을 노출/숨김.
//
// 파일 시스템 CSV 자체는 그대로 — 감사/재시작/diff 자산 유지. 여기는 마지막
// 결과 경로 *기억* 만 담당. 프로세스 수명 동안만 유효 (재시작 후엔 사용자가
// 다시 ① 을 돌리거나 ② 의 기본 경로를 그대로 사용).

namespace DocMine.UI;

public static class ScanResultRegistry
{
    private static readonly object _lock = new();
    private static string? _hwpLast;
    private static string? _pdfLast;

    public static string? HwpLast { get { lock (_lock) return _hwpLast; } }
    public static string? PdfLast { get { lock (_lock) return _pdfLast; } }

    public static void SetHwp(string path) { lock (_lock) _hwpLast = path; }
    public static void SetPdf(string path) { lock (_lock) _pdfLast = path; }
}
