// 진행 중인 비동기 작업이 있는 탭이 MainForm.FormClosing 에 협조하기 위한 contract.
// Python unified_gui.UnifiedApp._on_close 의 _busy 검사 + stop_event.set() 등가.

namespace DocMine.UI.Tabs;

public interface IBusyTab
{
    /// <summary>현재 비동기 작업 진행 중인지.</summary>
    bool IsBusy { get; }

    /// <summary>중지 요청 — CancellationToken 에 cancel 신호. 즉시 종료 보장 안 함.</summary>
    void RequestStop();
}
