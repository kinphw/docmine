// DocMine 메인 윈도우 — 7탭 TabControl.
// Python unified_gui.UnifiedApp 의 등가 + ⑤ 설정 추가.
//
// Phase 진행 상태:
//   ① HWP 스캔   ScanTab(Scope.Hwp)   — Phase 1
//   ① PDF 스캔   ScanTab(Scope.Pdf)   — Phase 1
//   ② HWP 적재   placeholder           — Phase 4
//   ② PDF 적재   PdfInsertTab          — Phase 3
//   ③ 검색       SearchTab             — Phase 2
//   ④ 추출       placeholder           — Phase 4
//   ⑤ 설정       SettingsTab           — settings.json 영속화

using DocMine.UI.Tabs;

namespace DocMine.UI;

public sealed class MainForm : Form
{
    private SettingsTab? _settingsTab;

    public MainForm()
    {
        Text = "DocMine";
        ClientSize = new Size(1180, 820);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUI();
        FormClosing += (_, _) => _settingsTab?.Flush();
    }

    private void BuildUI()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };

        tabs.TabPages.Add(new ScanTab(ScanTab.Scope.Hwp));
        tabs.TabPages.Add(new ScanTab(ScanTab.Scope.Pdf));
        tabs.TabPages.Add(MakePlaceholder("② HWP 적재", "Phase 4 에서 구현 (HWP COM 워커)"));
        tabs.TabPages.Add(new PdfInsertTab());
        tabs.TabPages.Add(new SearchTab());
        tabs.TabPages.Add(MakePlaceholder("④ 문서 추출", "Phase 4 에서 구현 (HWP/HWPX/PDF → TXT)"));

        _settingsTab = new SettingsTab();
        tabs.TabPages.Add(_settingsTab);

        Controls.Add(tabs);
    }

    private static TabPage MakePlaceholder(string title, string body)
    {
        var page = new TabPage(title);
        var lbl = new Label
        {
            Dock = DockStyle.Fill,
            Text = body,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
        };
        page.Controls.Add(lbl);
        return page;
    }
}
