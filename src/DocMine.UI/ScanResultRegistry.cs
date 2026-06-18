// 스캔 → 적재/검증 핸드오프용 레지스트리.
//
// ① 스캔 탭이 성공적으로 CSV 를 쓰면 그 경로를 여기 publish.
// 적재(HWP/PDF)·적재 전 검증 탭이 활성화될 때 이 값을 조회해 입력 CSV 를 제안.
//
// hwp+pdf 단일 스캔/단일 CSV 로 통합되며 슬롯도 하나(LastScanCsv)로 단순화.
// 파일 시스템 CSV 자체는 그대로 — 여기는 마지막 결과 경로 *기억* 만 담당.
// 프로세스 수명 동안만 유효.

namespace DocMine.UI;

public static class ScanResultRegistry
{
    private static readonly object _lock = new();
    private static string? _last;

    public static string? LastScanCsv { get { lock (_lock) return _last; } }

    public static void Set(string path) { lock (_lock) _last = path; }
}
