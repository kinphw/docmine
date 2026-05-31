// 한/글 COM 을 통한 텍스트 추출 — Python inserter.py:_com_extract 1:1.
//
// COM 인스턴스는 워커 수명 동안 재활용 (매 파일마다 spawn 비용 회피).
// COM_RESTART 카운터 도달 시 의도적 재시작 — 한/글 내부 메모리 누수 차단.

using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace DocMine.UI.Worker;

internal sealed class HwpComExtractor : IDisposable
{
    // COM 인스턴스 — dynamic late-bind (PIA 없이 IDispatch 호출).
    private dynamic? _hwp;
    private int _comCount;
    private readonly int _restartEvery;
    private readonly bool _killOnRestart;

    /// <param name="killOnRestart">COM 재활용·Dispose 시 PC 의 Hwp.exe 를 모두 정리할지.
    /// 배치 적재(HwpInsertRunner) 는 시작 시 이미 좀비 정리를 하므로 true.
    /// 인터랙티브 추출기(ExtractorTab) 는 사용자가 띄워둔 한/글 보호 위해 false.</param>
    public HwpComExtractor(int restartEvery = 500, bool killOnRestart = true)
    {
        _restartEvery = restartEvery;
        _killOnRestart = killOnRestart;
    }

    /// <summary>파일 1건 추출. 성공/실패 모두 호출자가 status 결정.</summary>
    public string Extract(string filePath)
    {
        var hwp = GetCom();
        try { hwp.SetMessageBoxMode(0x10000); } catch { }

        // DispatchEx(late-bind)는 optional 파라미터 디폴트가 안 채워지므로 3-arg 명시.
        // Open args (한컴 공식 — ';' 구분):
        //   - forceopen:true        손상/경고 파일도 강제 열기
        //   - versionwarning:false  "상위 버전에서 작성한 문서입니다" 팝업 차단
        //                           (한컴 공식 답변 bhjung@hancom 2023.11)
        hwp.Open(Path.GetFullPath(filePath), "", "forceopen:true;versionwarning:false");

        string text = "";
        try
        {
            string raw = hwp.GetTextFile("TEXT", "");
            if (!string.IsNullOrEmpty(raw))
                text = CleanHwpText(WebUtility.HtmlDecode(raw));
        }
        catch { }

        try { hwp.XHwpDocuments.Item(0).SetModified(false); } catch { }
        try { hwp.Run("FileClose"); }                          catch { }
        try { hwp.SetMessageBoxMode(0xF0000); }                catch { }

        _comCount++;
        if (_comCount % _restartEvery == 0)
        {
            ReleaseCom();
            if (_killOnRestart) KillHwpProcesses();
            Thread.Sleep(1000);
        }
        return text;
    }

    private dynamic GetCom()
    {
        if (_hwp is not null) return _hwp;

        var type = Type.GetTypeFromProgID("HwpFrame.HwpObject")
            ?? throw new InvalidOperationException(
                "HwpFrame.HwpObject ProgID 를 찾을 수 없습니다. 한/글이 설치되어 있는지 확인하세요.");
        _hwp = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("HwpFrame.HwpObject 인스턴스 생성 실패.");

        try { _hwp.RegisterModule("FilePathCheckDLL", "FilePathCheckerModule"); } catch { }
        try { _hwp.XHwpWindows.Item(0).Visible = false; } catch { }
        return _hwp;
    }

    private void ReleaseCom()
    {
        if (_hwp is null) return;
        try { Marshal.FinalReleaseComObject(_hwp); } catch { }
        _hwp = null;
        // GC 가 RCW 정리하도록 명시 호출.
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    public static void KillHwpProcesses()
    {
        foreach (var name in new[] { "Hwp", "HwpFrame" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    try { p.Dispose(); } catch { }
                }
            }
            catch { }
        }
    }

    public void Dispose()
    {
        ReleaseCom();
        if (_killOnRestart) KillHwpProcesses();
    }

    // ─ 텍스트 정제 ─ Python ComDocReader._clean_hwp_text 와 동등 ────

    private static readonly Dictionary<char, string> HwpCharMap = new()
    {
        ['◦'] = "◦", ['•'] = "•", ['●'] = "●", ['○'] = "○",
        [''] = "",  [''] = "",  [''] = "\n", [''] = "",
        [''] = "",  [''] = "",  [''] = "",  [' '] = " ",
    };

    private static readonly Regex CtrlRe = new(@"[\x00-\x08\x0c\x0e-\x1b]", RegexOptions.Compiled);

    public static string CleanHwpText(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (HwpCharMap.TryGetValue(ch, out var rep)) sb.Append(rep);
            else sb.Append(ch);
        }
        var cleaned = CtrlRe.Replace(sb.ToString(), "");
        return cleaned.Trim();
    }
}
