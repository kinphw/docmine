// DocMine 메인 윈도우.
//
// 구성:
//   상단 바: About(?) 버튼 — 우상단
//   본체:    8탭 TabControl (Fill)
//
// 종료 처리 (FormClosing):
//   - 비동기 작업(스캔/적재/추출) 진행 중이면 확인 다이얼로그
//   - "예" 면 IBusyTab.RequestStop() 호출 + 잠시 polling 후 닫음
//   - 시간 초과 시 강제 닫음 (Job Object 가 워커 회수)
//   Python unified_gui._on_close / _poll_close 등가.

using DocMine.UI.Tabs;

namespace DocMine.UI;

public sealed class MainForm : Form
{
    private SettingsTab? _settingsTab;
    private TabControl? _tabs;

    public MainForm()
    {
        Text = "DocMine";
        ClientSize = new Size(1180, 820);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = AppIcon.Build(); } catch { /* 아이콘 실패는 치명적 아님 */ }

        BuildUI();
        FormClosing += OnFormClosing;
    }

    private void BuildUI()
    {
        SuspendLayout();

        // WinForms Dock layout 은 Controls 컬렉션의 reverse-index 순으로 영역 할당:
        // → Fill 컨트롤을 *먼저* Add 해야 큰 영역 차지하고, Top/Bottom 등을 나중 Add 하면
        //    Top 이 먼저 영역 잡고 Fill 이 남은 영역에 들어가는 표준 동작.

        // ── ① Fill: 탭 컨트롤 ─────────────────────────────────────────
        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(new ScanTab(ScanTab.Scope.Hwp));
        _tabs.TabPages.Add(new ScanTab(ScanTab.Scope.Pdf));
        _tabs.TabPages.Add(new HwpInsertTab());
        _tabs.TabPages.Add(new PdfInsertTab());
        _tabs.TabPages.Add(new SearchTab());
        _tabs.TabPages.Add(new ExtractorTab());
        _tabs.TabPages.Add(new DbExportTab());
        _settingsTab = new SettingsTab();
        _tabs.TabPages.Add(_settingsTab);
        Controls.Add(_tabs);

        // ── ② Top: ? About 버튼만 있는 얇은 바 ───────────────────────
        var topbar = new Panel { Dock = DockStyle.Top, Height = 32 };
        var aboutBtn = new Button
        {
            Text = "?",
            Width = 32, Height = 26,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Top = 3,
            FlatStyle = FlatStyle.System,
        };
        aboutBtn.Click += (_, _) => AboutForm.Open(this);
        topbar.Controls.Add(aboutBtn);
        topbar.Resize += (_, _) => aboutBtn.Left = topbar.Width - aboutBtn.Width - 10;
        // 초기 위치 (Resize 가 처음엔 안 불릴 수 있음)
        topbar.HandleCreated += (_, _) => aboutBtn.Left = topbar.Width - aboutBtn.Width - 10;
        Controls.Add(topbar);

        ResumeLayout(performLayout: true);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _settingsTab?.Flush();

        var busy = CollectBusyTabs();
        if (busy.Count == 0) return;

        var ok = MessageBox.Show(
            this,
            "스캔/적재/추출 작업이 진행 중입니다.\n그래도 창을 닫겠습니까?\n" +
            "(중지 신호 후 최대 8초 대기 → 시간 초과 시 강제 종료. Job Object 가 워커를 회수합니다.)",
            "진행 중 작업",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ok != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        foreach (var b in busy) { try { b.RequestStop(); } catch { } }

        // 일단 close 막고 polling 시작. 8초 후 강제 close.
        e.Cancel = true;
        PollCloseAsync(8000);
    }

    private List<IBusyTab> CollectBusyTabs()
    {
        var result = new List<IBusyTab>();
        if (_tabs is null) return result;
        foreach (TabPage page in _tabs.TabPages)
            if (page is IBusyTab b && b.IsBusy) result.Add(b);
        return result;
    }

    private async void PollCloseAsync(int timeoutMs)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            await Task.Delay(200);
            if (CollectBusyTabs().Count == 0) break;
        }
        // 시간 초과여도 그대로 close — Job Object 가 워커 정리.
        FormClosing -= OnFormClosing;  // 중복 confirm 방지
        Close();
    }
}
