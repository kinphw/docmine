// ProgressEta — 적재 진행 라인의 ETA(예상 잔여시간) 계산기.
// Python inserter.py 의 progress ETA (`ETA {분}:{초}`) 등가 — C# 포팅 때 빠졌던 것 복원.
//
// 주의: PDF/HWP 적재는 시작 시 DB 기존 파일 skip 을 bulk 로 한 번에 카운트한다
// (Index 가 즉시 skipCount 만큼 점프). 시작 시점부터 단순히 elapsed/Index 평균을 내면
// ETA 가 크게 왜곡된다. 그래서 '실제 처리 시작' 이후의 증분(delta)으로 속도를 측정한다
// — 첫 progress 콜백의 Index 를 기준점으로 잡고, 이후 처리량/경과로 잔여를 외삽한다.

using System.Diagnostics;

namespace DocMine.UI;

public sealed class ProgressEta
{
    private readonly Stopwatch _sw = new();
    private int _baseIndex;
    private bool _started;

    /// <summary>새 적재 시작 전 호출 — 기준점/타이머 초기화.</summary>
    public void Reset()
    {
        _sw.Reset();
        _started = false;
        _baseIndex = 0;
    }

    /// <summary>진행 라인에 덧붙일 ETA 문자열(예 "  ETA 12:30") 반환. 추정 불가하면 빈 문자열.</summary>
    public string Format(int index, int total)
    {
        if (!_started)
        {
            // 보통 bulk skip 직후의 첫 콜백 — 여기를 0점으로 잡아 이후 증분만 속도에 반영.
            _baseIndex = index;
            _sw.Restart();
            _started = true;
            return "";
        }

        var processed = index - _baseIndex;
        var elapsed = _sw.Elapsed.TotalSeconds;
        if (processed <= 0 || elapsed <= 0) return "";

        var rate = processed / elapsed;            // 건/초
        var remaining = Math.Max(0, total - index);
        return "  ETA " + FormatSpan(remaining / rate);
    }

    private static string FormatSpan(double seconds)
    {
        if (double.IsInfinity(seconds) || seconds < 0) return "?";
        if (seconds >= 3600)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        }
        var m = (int)(seconds / 60);
        var s = (int)(seconds % 60);
        return $"{m}:{s:00}";
    }
}
