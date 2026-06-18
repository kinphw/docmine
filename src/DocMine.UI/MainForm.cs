// DocMine 메인 윈도우.
//
// 구성:
//   상단 바: About(?) 버튼 — 우상단
//   좌측:    그룹 사이드 네비 (파이프라인 / 조회 / 도구 / 설정)
//   우측:    선택된 기능 콘텐츠 (탭 인스턴스 재사용 — 상태 유지)
//
// 가로 평면 탭이 늘어 어색해진 IA 를 그룹 네비로 재편. 각 기능은 TabPage
// 인스턴스 그대로(콘텐츠 호스트에 Dock=Fill, Visible 토글로 전환).
//
// 종료 처리 (FormClosing):
//   - 비동기 작업(스캔/적재/추출) 진행 중이면 확인 다이얼로그
//   - "예" 면 IBusyTab.RequestStop() 호출 + 잠시 polling 후 닫음
//   - 시간 초과 시 강제 닫음 (Job Object 가 워커 회수)

using System.Drawing;
using DocMine.UI.Tabs;

namespace DocMine.UI;

public sealed class MainForm : Form
{
    private SettingsTab? _settingsTab;
    private readonly List<Control> _contents = new();   // IBusyTab 순회 대상
    private Panel _host = null!;
    private Button? _currentNav;

    private static readonly Color NavBg       = Color.FromArgb(0x2b, 0x2b, 0x2b);
    private static readonly Color NavHeaderFg = Color.FromArgb(0x9a, 0xa0, 0xa6);
    private static readonly Color NavItemFg   = Color.FromArgb(0xe0, 0xe0, 0xe0);
    private static readonly Color NavSelBg    = Color.FromArgb(0x0e, 0x63, 0x9c);

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

        // 기능 탭 인스턴스 (재사용 — 전환해도 상태 유지).
        var scan      = new ScanTab();
        var insert    = new InsertTab();
        var preflight = new PreflightTab();
        var search    = new SearchTab();
        var extractor = new ExtractorTab();
        var dbExport  = new DbExportTab();
        _settingsTab  = new SettingsTab();

        // ── ① Fill: 콘텐츠 호스트 ─────────────────────────────────────
        // 선택된 탭만 _host 에 Add (나머지는 Remove — 인스턴스는 _contents 에
        // 보관해 상태 유지). TabPage.Visible 토글은 TabControl 밖에서 불안정해
        // Controls 교체 방식을 쓴다.
        _host = new Panel { Dock = DockStyle.Fill };
        foreach (var c in new Control[] { scan, insert, preflight, search, extractor, dbExport, _settingsTab })
        {
            c.Dock = DockStyle.Fill;
            _contents.Add(c);
        }
        Controls.Add(_host);

        // ── ② Left: 그룹 사이드 네비 ──────────────────────────────────
        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 178,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = NavBg,
            Padding = new Padding(0, 6, 0, 6),
            AutoScroll = true,
        };
        AddNavGroup(nav, "파이프라인", ("스캔", scan), ("적재", insert), ("적재 전 검증", preflight), ("반출", dbExport));
        AddNavGroup(nav, "조회", ("검색", search));
        AddNavGroup(nav, "도구", ("문서 추출", extractor));
        AddNavGroup(nav, "설정", ("DB 설정", _settingsTab));
        Controls.Add(nav);

        // ── ③ Top: ? About 버튼만 있는 얇은 바 ───────────────────────
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
        topbar.HandleCreated += (_, _) => aboutBtn.Left = topbar.Width - aboutBtn.Width - 10;
        Controls.Add(topbar);

        ResumeLayout(performLayout: true);

        // 초기 화면 = 스캔.
        ShowContent(scan, (Button)((FlowLayoutPanel)nav).Controls[1]);
    }

    // 그룹 헤더(비클릭) + 항목 버튼들을 네비에 추가.
    private void AddNavGroup(FlowLayoutPanel nav, string header, params (string Label, Control Content)[] items)
    {
        nav.Controls.Add(new Label
        {
            Text = header,
            AutoSize = false,
            Width = 170,
            Height = 24,
            ForeColor = NavHeaderFg,
            Font = new Font("맑은 고딕", 8.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            Margin = new Padding(0, 6, 0, 2),
        });
        foreach (var (label, content) in items)
        {
            var btn = new Button
            {
                Text = "   " + label,
                Width = 170,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = NavItemFg,
                BackColor = NavBg,
                Margin = new Padding(4, 0, 4, 0),
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += (_, _) => ShowContent(content, btn);
            nav.Controls.Add(btn);
        }
    }

    private void ShowContent(Control content, Button navBtn)
    {
        if (!_host.Controls.Contains(content))
        {
            _host.Controls.Clear();          // 인스턴스는 _contents 가 보관 — Dispose 안 됨
            _host.Controls.Add(content);
        }

        if (_currentNav is not null)
        {
            _currentNav.BackColor = NavBg;
            _currentNav.ForeColor = NavItemFg;
        }
        navBtn.BackColor = NavSelBg;
        navBtn.ForeColor = Color.White;
        _currentNav = navBtn;
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
        foreach (var c in _contents)
            if (c is IBusyTab b && b.IsBusy) result.Add(b);
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
