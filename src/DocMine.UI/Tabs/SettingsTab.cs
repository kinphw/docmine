// SettingsTab — MariaDB 접속 정보만 영속화.
//
// 그 외 모든 값(CSV 경로/적재 튜닝/스캔 드라이브) 은 코드 상수 또는 런타임 자동
// 결정이라 GUI 노출 불필요. 박건영님의 운영 환경에서 PC 마다 다르게 가는 건
// DB 접속 정보뿐.
//
// 저장 정책 (Forge UserSettings 패턴):
//   - Leave 이벤트 = 사용자 의도 확정 시점 → 즉시 디스크 write + AppConfig.Reload().
//   - "지금 저장" 버튼 = 명시적 flush 안전망.
//   - "연결 테스트" = 저장 없이 임시 인스턴스로 검증.
//   - MainForm.FormClosing 에서 한 번 더 Flush() (잔량 보장).

using DocMine.Core.Config;

namespace DocMine.UI.Tabs;

public sealed class SettingsTab : TabPage
{
    private readonly UserSettingsData _data;

    private readonly TextBox _hostBox, _portBox, _userBox, _pwBox, _dbBox, _tableBox;
    private readonly CheckBox _showPwBox;
    private readonly Button _testBtn;
    private readonly Label _testResult;
    private readonly Label _statusLabel;
    private readonly ListBox _excludeList;
    private readonly TextBox _pdfWorkersBox;

    public SettingsTab() : base("⑤ 설정")
    {
        _data = UserSettings.Load();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(12),
            AutoScroll = true,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // ── DB ───────────────────────────────────────────────────────
        var dbGroup = MakeGroup("MariaDB 접속");
        var dbGrid = MakeGrid();
        AddLabel(dbGrid, 0, "호스트");     _hostBox  = AddTextBox(dbGrid, 0, _data.DbHost);
        AddLabel(dbGrid, 1, "포트");       _portBox  = AddTextBox(dbGrid, 1, _data.DbPort.ToString(), width: 80);
        AddLabel(dbGrid, 2, "사용자");     _userBox  = AddTextBox(dbGrid, 2, _data.DbUser);
        AddLabel(dbGrid, 3, "비밀번호");
        var pwRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
        _pwBox = new TextBox { Width = 240, UseSystemPasswordChar = true, Text = _data.DbPassword };
        _showPwBox = new CheckBox { Text = "표시", AutoSize = true, Margin = new Padding(6, 4, 0, 0) };
        _showPwBox.CheckedChanged += (_, _) => _pwBox.UseSystemPasswordChar = !_showPwBox.Checked;
        pwRow.Controls.Add(_pwBox);
        pwRow.Controls.Add(_showPwBox);
        dbGrid.Controls.Add(pwRow, 1, 3);
        AddLabel(dbGrid, 4, "DB 이름");    _dbBox    = AddTextBox(dbGrid, 4, _data.DbName);
        AddLabel(dbGrid, 5, "테이블");     _tableBox = AddTextBox(dbGrid, 5, _data.DbTable);

        _testBtn = new Button { Text = "연결 테스트", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        _testBtn.Click += (_, _) => OnTestConnection();
        _testResult = new Label { AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(8, 9, 0, 0) };
        var testRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        testRow.Controls.Add(_testBtn);
        testRow.Controls.Add(_testResult);

        var dbContainer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        dbContainer.Controls.Add(dbGrid);
        dbContainer.Controls.Add(testRow);
        dbGroup.Controls.Add(dbContainer);
        root.Controls.Add(dbGroup, 0, 0);

        // ── 스캔 예외 폴더 (HWP/PDF 공용) ──────────────────────────────
        var excludeGroup = MakeGroup("스캔 예외 폴더 (하위 폴더 포함)");
        var excludeContainer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        var excludeHint = new Label
        {
            Text = "여기 등록된 폴더와 그 하위는 HWP/PDF 스캔에서 모두 제외됩니다.\n" +
                   "OneDrive 같은 동기화 폴더에 파일이 너무 많을 때 사용.",
            ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(0, 0, 0, 4),
        };
        _excludeList = new ListBox
        {
            Height = 120, Width = 700,
            SelectionMode = SelectionMode.MultiExtended,
            Font = new Font("맑은 고딕", 9),
        };
        foreach (var d in _data.ScanExcludeDirs) _excludeList.Items.Add(d);

        var excludeBtns = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        var addBtn = new Button { Text = "폴더 추가…", AutoSize = true };
        addBtn.Click += (_, _) => OnAddExclude();
        var removeBtn = new Button { Text = "선택 제거", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        removeBtn.Click += (_, _) => OnRemoveExclude();
        excludeBtns.Controls.Add(addBtn);
        excludeBtns.Controls.Add(removeBtn);

        excludeContainer.Controls.Add(excludeHint);
        excludeContainer.Controls.Add(_excludeList);
        excludeContainer.Controls.Add(excludeBtns);
        excludeGroup.Controls.Add(excludeContainer);
        root.Controls.Add(excludeGroup, 0, 1);

        // ── PDF 적재 튜닝 ─────────────────────────────────────────────
        var tuneGroup = MakeGroup("PDF 적재 튜닝");
        var tuneContainer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        var tuneHint = new Label
        {
            Text = "PDF 워커 프로세스 수 (0 = 자동: 논리 CPU 수).\n" +
                   "메모리 압박/silent crash 시 보수적으로 줄임 (예: 2~4).",
            ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(0, 0, 0, 4),
        };
        var tuneRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        tuneRow.Controls.Add(new Label { Text = "PDF 워커 수", AutoSize = true, Padding = new Padding(0, 4, 8, 0) });
        _pdfWorkersBox = new TextBox { Width = 60, Text = _data.PdfWorkers.ToString() };
        tuneRow.Controls.Add(_pdfWorkersBox);

        tuneContainer.Controls.Add(tuneHint);
        tuneContainer.Controls.Add(tuneRow);
        tuneGroup.Controls.Add(tuneContainer);
        root.Controls.Add(tuneGroup, 0, 2);

        // ── 하단 액션 + 상태 ────────────────────────────────────────────
        var bot = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var saveBtn = new Button { Text = "지금 저장", AutoSize = true };
        saveBtn.Click += (_, _) => SaveNow(verbose: true);
        var openFolderBtn = new Button { Text = "설정 폴더 열기", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        openFolderBtn.Click += (_, _) => OpenSettingsFolder();
        var pathLbl = new Label
        {
            Text = $"  {UserSettings.SettingsPath()}",
            AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(8, 6, 0, 0),
        };
        bot.Controls.Add(saveBtn);
        bot.Controls.Add(openFolderBtn);
        bot.Controls.Add(pathLbl);
        root.Controls.Add(bot, 0, 3);

        _statusLabel = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(0, 4, 0, 0),
        };
        root.Controls.Add(_statusLabel, 0, 4);

        // 안내 텍스트.
        var note = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Gray,
            Padding = new Padding(0, 12, 0, 0),
            Text = "• CSV 경로 · 스캔 드라이브 · 적재 튜닝은 코드 기본값을 사용하며 별도 저장하지 않습니다.\n" +
                   "• CSV 출력 파일명은 '스캔' 탭에서 매 실행마다 직접 바꿀 수 있습니다.\n" +
                   "• 스캔 드라이브는 '스캔' 탭에서 마운트된 드라이브 목록을 보고 직접 선택합니다.",
        };
        root.Controls.Add(note, 0, 5);

        Controls.Add(root);

        // ─ Leave 이벤트로 자동 저장 ─ Forge UserSettings 패턴.
        foreach (var tb in new[] { _hostBox, _portBox, _userBox, _pwBox, _dbBox, _tableBox, _pdfWorkersBox })
            tb.Leave += (_, _) => SaveNow(verbose: false);
    }

    /// <summary>MainForm.FormClosing 에서 호출 — 잔량 강제 flush.</summary>
    public void Flush() => SaveNow(verbose: false);

    private void SaveNow(bool verbose)
    {
        try
        {
            ApplyFromUi();
            UserSettings.Save(_data);
            AppConfig.Reload();
            _statusLabel.Text = $"저장됨 — {DateTime.Now:HH:mm:ss}";
            _statusLabel.ForeColor = Color.SeaGreen;
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"저장 실패: {ex.Message}";
            _statusLabel.ForeColor = Color.Firebrick;
            if (verbose) MessageBox.Show(this, ex.Message, "설정 저장 실패");
        }
    }

    private void ApplyFromUi()
    {
        _data.DbHost = _hostBox.Text.Trim();
        _data.DbPort = int.TryParse(_portBox.Text, out var p) ? p : 3306;
        _data.DbUser = _userBox.Text.Trim();
        _data.DbPassword = _pwBox.Text;
        _data.DbName = _dbBox.Text.Trim();
        _data.DbTable = _tableBox.Text.Trim();
        _data.ScanExcludeDirs = _excludeList.Items.Cast<string>().ToList();
        _data.PdfWorkers = int.TryParse(_pdfWorkersBox.Text, out var pw) ? Math.Max(0, pw) : 0;
    }

    private void OnAddExclude()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "스캔 예외 폴더 선택 (하위 폴더 포함됨)",
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var picked = dlg.SelectedPath.TrimEnd('\\');
        // 중복 방지 (대소문자 무시).
        foreach (var item in _excludeList.Items.Cast<string>())
        {
            if (string.Equals(item.TrimEnd('\\'), picked, StringComparison.OrdinalIgnoreCase))
                return;
        }
        _excludeList.Items.Add(picked);
        SaveNow(verbose: false);
    }

    private void OnRemoveExclude()
    {
        var indices = _excludeList.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
        if (indices.Count == 0) return;
        foreach (var i in indices) _excludeList.Items.RemoveAt(i);
        SaveNow(verbose: false);
    }

    private void OnTestConnection()
    {
        ApplyFromUi();
        _testResult.Text = "테스트 중…";
        _testResult.ForeColor = Color.Gray;
        try
        {
            var probe = new MySqlConnector.MySqlConnection(
                string.Join(";",
                    $"Server={_data.DbHost}",
                    $"Port={_data.DbPort}",
                    $"User Id={_data.DbUser}",
                    $"Password={_data.DbPassword}",
                    $"Database={_data.DbName}",
                    "CharSet=utf8mb4",
                    "Connection Timeout=5"));
            probe.Open();
            probe.Dispose();
            _testResult.Text = "✓ 연결 성공";
            _testResult.ForeColor = Color.SeaGreen;
        }
        catch (Exception ex)
        {
            _testResult.Text = $"✗ {ex.Message}";
            _testResult.ForeColor = Color.Firebrick;
        }
    }

    private static void OpenSettingsFolder()
    {
        var dir = UserSettings.SettingsDir();
        Directory.CreateDirectory(dir);
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { /* 운영 환경에서 ShellExecute 막혀 있어도 무시 */ }
    }

    // ─ Layout 헬퍼 ─────────────────────────────────────────────────

    private static GroupBox MakeGroup(string title) => new()
    {
        Text = title, Dock = DockStyle.Top, AutoSize = true,
        Padding = new Padding(12), Margin = new Padding(0, 0, 0, 8),
    };

    private static TableLayoutPanel MakeGrid()
    {
        var g = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 2, RowCount = 1,
            Padding = new Padding(0, 4, 0, 4),
        };
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return g;
    }

    private static void AddLabel(TableLayoutPanel g, int row, string text)
    {
        if (g.RowCount <= row) g.RowCount = row + 1;
        g.Controls.Add(new Label
        {
            Text = text, AutoSize = true,
            Padding = new Padding(0, 5, 12, 5),
        }, 0, row);
    }

    private static TextBox AddTextBox(TableLayoutPanel g, int row, string text, int width = 240)
    {
        var tb = new TextBox { Text = text, Width = width, Margin = new Padding(0, 2, 0, 2) };
        g.Controls.Add(tb, 1, row);
        return tb;
    }
}
