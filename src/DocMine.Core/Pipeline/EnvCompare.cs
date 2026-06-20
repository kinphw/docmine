// EnvCompare — 두 문서 집합(현재환경 ↔ 대조본)의 합집합 대조 로직.
//
// 반출/반입 탭이 공유하는 "환경 대조" 의 순수 코어 (COM/GUI 무의존, 단위 검증 대상).
//   base    = 액션 대상 집합 (반출: 현재 DB 행 / 반입: CSV 레코드)
//   compare = 상대 키셋      (반출: 매니페스트 / 반입: 현재 env 키)
// 결과: 각 base 의 대조 포함여부(InCompare) + 대조에만 있는 키(유령 행).
// 키는 (directory, filename) NormKey. nameOnly=true 면 폴더 무시 파일명만 비교.

namespace DocMine.Core.Pipeline;

public static class EnvCompare
{
    public readonly record struct Marked<T>(T Item, bool InCompare);

    public sealed record Union<T>(
        IReadOnlyList<Marked<T>> Base,                       // base 전체 + 대조 포함여부
        IReadOnlyList<(string Dir, string Fn)> CompareOnly); // 대조에만 있는 키(유령 행)

    public static Union<T> Build<T>(
        IReadOnlyList<T> baseItems,
        Func<T, (string Dir, string Fn)> keyOf,
        IReadOnlyCollection<(string Dir, string Fn)> compareKeys,
        bool nameOnly)
    {
        // 대조 측 매칭 인덱스.
        var compareByKey  = new HashSet<(string, string)>();
        var compareByName = new HashSet<string>();
        foreach (var k in compareKeys)
        {
            compareByKey.Add(CsvIngestHelpers.NormKey(k.Dir, k.Fn));
            compareByName.Add(CsvIngestHelpers.NormName(k.Fn));
        }

        // base 측 매칭 인덱스 (유령 행 산출용).
        var baseByKey  = new HashSet<(string, string)>();
        var baseByName = new HashSet<string>();

        var marked = new List<Marked<T>>(baseItems.Count);
        foreach (var item in baseItems)
        {
            var (dir, fn) = keyOf(item);
            var nk = CsvIngestHelpers.NormKey(dir, fn);
            var nn = CsvIngestHelpers.NormName(fn);
            baseByKey.Add(nk);
            baseByName.Add(nn);
            bool inCompare = nameOnly ? compareByName.Contains(nn) : compareByKey.Contains(nk);
            marked.Add(new Marked<T>(item, inCompare));
        }

        // 대조에만 있는 키 — base 에 없는 것만. nameOnly 면 파일명 기준으로 dedup.
        var compareOnly = new List<(string, string)>();
        var seenGhost = new HashSet<string>();   // nameOnly dedup 용
        foreach (var k in compareKeys)
        {
            var nn = CsvIngestHelpers.NormName(k.Fn);
            bool inBase = nameOnly ? baseByName.Contains(nn)
                                   : baseByKey.Contains(CsvIngestHelpers.NormKey(k.Dir, k.Fn));
            if (inBase) continue;
            if (nameOnly && !seenGhost.Add(nn)) continue;   // 같은 파일명 유령 1줄
            compareOnly.Add(k);
        }

        return new Union<T>(marked, compareOnly);
    }
}
