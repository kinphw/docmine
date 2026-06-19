// 한/글 COM 을 통한 텍스트 추출 — Python inserter.py:_com_extract 1:1.
//
// COM 인스턴스는 워커 수명 동안 재활용 (매 파일마다 spawn 비용 회피).
// COM_RESTART 카운터 도달 시 의도적 재시작 — 한/글 내부 메모리 누수 차단.

using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using DocMine.Core.Diff;

namespace DocMine.UI.Worker;

internal sealed class HwpComExtractor : IDisposable
{
    // COM 인스턴스 — dynamic late-bind (PIA 없이 IDispatch 호출).
    private dynamic? _hwp;
    private int _comCount;
    private readonly int _restartEvery;
    private readonly bool _killOnRestart;

    /// <summary>직전 SaveAsHwpx 가 연 문서의 페이지 수(렌더 결과). 못 읽으면 0.</summary>
    public int LastPageCount { get; private set; }

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

    /// <summary>
    /// 파일을 COM 으로 열어 HWPX 로 SaveAs — 바이너리 .hwp / DRM 을 구조 파서(ZIP) 로
    /// 흘려보내기 위한 정규화. 성공 시 임시 .hwpx 경로 반환(호출자가 삭제 책임).
    /// </summary>
    /// <remarks>
    /// 주의: DRM/DLP 환경에서는 디스크에 쓴 임시본이 자동 재암호화될 수 있다.
    /// 그 경우 호출자(ZIP 파서) 가 DRM 으로 인식해 실패하므로 평문 비교로 폴백한다.
    /// </remarks>
    public string SaveAsHwpx(string filePath, string outDir)
    {
        var hwp = GetCom();
        try { hwp.SetMessageBoxMode(0x10000); } catch { }

        hwp.Open(Path.GetFullPath(filePath), "", "forceopen:true;versionwarning:false");

        // 페이지 수(렌더 결과) — 비교 요약 표기용. 버전/문서에 따라 0 일 수 있어 best-effort.
        LastPageCount = 0;
        try { LastPageCount = Convert.ToInt32(hwp.PageCount); } catch { }

        var outPath = Path.Combine(outDir, $"docmine_cmp_{Guid.NewGuid():N}.hwpx");

        bool ok;
        try
        {
            // 한컴 COM: SaveAs(Path, Format, arg). HWPX = OWPML(zip) 포맷.
            object? rv = hwp.SaveAs(outPath, "HWPX", "");
            ok = rv switch { bool b => b, null => false, _ => Convert.ToBoolean(rv) };
        }
        catch
        {
            ok = false;
        }
        finally
        {
            try { hwp.XHwpDocuments.Item(0).SetModified(false); } catch { }
            try { hwp.Run("FileClose"); }                          catch { }
            try { hwp.SetMessageBoxMode(0xF0000); }                catch { }
        }

        _comCount++;
        if (_comCount % _restartEvery == 0)
        {
            ReleaseCom();
            if (_killOnRestart) KillHwpProcesses();
            Thread.Sleep(1000);
        }

        if (!ok)
            throw new InvalidOperationException(
                $"SaveAs HWPX 실패 — 이 한/글 버전이 HWPX 내보내기를 지원하지 않거나 거부함: {Path.GetFileName(filePath)}");
        if (!File.Exists(outPath))
            throw new InvalidOperationException($"SaveAs 결과 파일이 없습니다: {outPath}");

        return outPath;
    }

    /// <summary>
    /// 비교 결과(ReportDoc)를 새 한/글 문서로 만들어 저장 — 글자색으로 추가/삭제/수정 구분.
    /// (forge 의 COM 텍스트 삽입·서식 패턴 차용: CreateAction 5단계 + CharShape TextColor.)
    /// </summary>
    /// <param name="format">"HWP" 또는 "HWPX".</param>
    public void WriteReport(ReportDoc doc, string outPath, string format)
    {
        var hwp = GetCom();
        try { hwp.SetMessageBoxMode(0x10000); } catch { }

        try
        {
            hwp.Run("FileNew");

            foreach (var run in doc.Runs)
            {
                if (!string.IsNullOrEmpty(run.Text))
                {
                    // 런마다 모든 글자속성을 명시 — 직전 런 상태가 새어나가지 않게.
                    SetCharShape(hwp, run.R, run.G, run.B, run.Bold, run.Strike, run.Underline);
                    InsertText(hwp, run.Text);
                }
                for (int i = 0; i < run.Breaks; i++)
                    hwp.HAction.Run("BreakPara");
            }

            var outFull = Path.GetFullPath(outPath).Replace('/', '\\');
            bool ok = SaveAs(hwp, outFull, format);

            try { hwp.XHwpDocuments.Item(0).SetModified(false); } catch { }
            try { hwp.Run("FileClose"); }                          catch { }

            if (!ok)
                throw new InvalidOperationException(
                    $"리포트 SaveAs({format}) 실패 — 한/글이 해당 포맷 저장을 거부했습니다.");
            if (!File.Exists(outFull))
                throw new InvalidOperationException($"리포트 파일이 생성되지 않았습니다: {outFull}");
        }
        finally
        {
            try { hwp.SetMessageBoxMode(0xF0000); } catch { }
        }

        _comCount++;
        if (_comCount % _restartEvery == 0)
        {
            ReleaseCom();
            if (_killOnRestart) KillHwpProcesses();
            Thread.Sleep(1000);
        }
    }

    // ─ COM 편집 보조 (forge ComHelpers/Primitives 패턴) ──────────────────

    private static void InsertText(dynamic hwp, string text)
    {
        var act = hwp.CreateAction("InsertText");
        var s = act.CreateSet();
        act.GetDefault(s);
        s.SetItem("Text", text);
        act.Execute(s);
    }

    // 색·굵기·취소선·밑줄을 한 번의 CharShape 액션으로 명시 설정(이후 입력 글자에 적용).
    // 항목명이 버전마다 다를 수 있어 SetItem 은 개별 try — 일부 실패해도 나머지는 적용.
    private static void SetCharShape(dynamic hwp, int r, int g, int b, bool bold, bool strike, bool underline)
    {
        var act = hwp.CreateAction("CharShape");
        var s = act.CreateSet();
        act.GetDefault(s);
        try { s.SetItem("TextColor", hwp.RGBColor(r, g, b)); } catch { }
        try { s.SetItem("Bold", bold ? 1 : 0); } catch { }
        try { s.SetItem("UnderlineType", underline ? 1 : 0); } catch { }
        try { s.SetItem("StrikeOutType", strike ? 1 : 0); } catch { }
        act.Execute(s);
    }

    private static bool SaveAs(dynamic hwp, string path, string format)
    {
        try
        {
            object? rv = hwp.SaveAs(path, format, "");
            return rv switch { bool b => b, null => false, _ => Convert.ToBoolean(rv) };
        }
        catch
        {
            try { hwp.SaveAs(path); return File.Exists(path); } catch { return false; }
        }
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
