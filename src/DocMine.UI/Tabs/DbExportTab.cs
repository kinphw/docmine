// DbExportTab — ⑤ DB 추출.
//
// ④ 추출(ExtractorTab) 이 '파일 본문 → TXT' 인 것과 달리, 이 탭은
// 'DB 레코드(메타데이터) → CSV' 다. 요건:
//   1) 특정 요건으로 검색 (키워드 + 대상/방식 + 적재일 범위)
//   2) 행별 체크박스 선택 + 전체 선택/해제
//   3) 선택 행을 CSV 로 추출 (저장 파일명/폴더 지정)
//
// 검색은 SearchService.SearchForExport (body_text 제외, 메타 전체 컬럼) 사용.
// 페이징 없이 안전 상한(200,000)까지 한 번에 — 추출은 필터링된 결과가 대상.

using DocMine.Core.Config;
using DocMine.Core.Db;

namespace DocMine.UI.Tabs;

public sealed class DbExportTab : TabPage
{
    private readonly SearchService _search;
    private readonly DocumentRepository _repo;

    private readonly TextBox _keywordBox;
    private readonly RadioButton _targetBoth, _targetTitle, _targetBody;
    private readonly RadioButton _modeAnd, _modeOr, _modePhrase;
    private readonly CheckBox _includeExcludedBox;
    private readonly CheckBox _dateFilterBox;
    private readonly DateTimePicker _dateFrom, _dateTo;
    private readonly Button _searchBtn;
    private readonly Label _statusLabel;

    private readonly ListView _list;
    private readonly LogPane _log;

    private readonly Button _selectAllBtn, _selectNoneBtn, _exportBtn;
    private readonly Label _selInfoLabel;

    public DbExportTab() : base("⑤ DB 추출")
    {
        var cfg = AppConfig.Current;
        _search = new SearchService(cfg);
        _repo = new DocumentRepository(cfg);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 검색 컨트롤
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 결과
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 로그

        // ── 검색 컨트롤 ────────────────────────────────────────────────
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 3 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // row1: 키워드 + 검색.
        var row1 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        row1.Controls.Add(new Label { Text = "키워드:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _keywordBox = new TextBox { Width = 320, Font = new Font("맑은 고딕", 11) };
        _keywordBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = DoSearchAsync(); } };
        row1.Controls.Add(_keywordBox);
        _searchBtn = new Button { Text = "검색", AutoSize = true, Margin = new Padding(8, 2, 0, 0) };
        _searchBtn.Click += async (_, _) => await DoSearchAsync();
        row1.Controls.Add(_searchBtn);
        _statusLabel = new Label { Text = "", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(12, 6, 0, 0) };
        row1.Controls.Add(_statusLabel);
        top.Controls.Add(row1, 0, 0);

        // row2: 대상 + 방식 + 제외 포함.
        var row2 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        row2.Controls.Add(new Label { Text = "검색 대상:", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        _targetBoth  = new RadioButton { Text = "제목+본문", Checked = true, AutoSize = true };
        _targetTitle = new RadioButton { Text = "제목만",    AutoSize = true };
        _targetBody  = new RadioButton { Text = "본문만",    AutoSize = true };
        row2.Controls.Add(_targetBoth);
        row2.Controls.Add(_targetTitle);
        row2.Controls.Add(_targetBody);
        row2.Controls.Add(new Label { Text = "  |  ", AutoSize = true, ForeColor = Color.LightGray, Padding = new Padding(0, 4, 0, 0) });
        row2.Controls.Add(new Label { Text = "방식:", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        _modeAnd    = new RadioButton { Text = "AND", Checked = true, AutoSize = true };
        _modeOr     = new RadioButton { Text = "OR",  AutoSize = true };
        _modePhrase = new RadioButton { Text = "전체 문자열", AutoSize = true };
        row2.Controls.Add(_modeAnd);
        row2.Controls.Add(_modeOr);
        row2.Controls.Add(_modePhrase);
        row2.Controls.Add(new Label { Text = "  |  ", AutoSize = true, ForeColor = Color.LightGray, Padding = new Padding(0, 4, 0, 0) });
        _includeExcludedBox = new CheckBox { Text = "제외 항목 포함", Checked = true, AutoSize = true };
        row2.Controls.Add(_includeExcludedBox);
        top.Controls.Add(row2, 0, 1);

        // row3: 적재일 범위.
        var row3 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        _dateFilterBox = new CheckBox { Text = "적재일 범위", AutoSize = true, Padding = new Padding(0, 4, 4, 0) };
        _dateFilterBox.CheckedChanged += (_, _) => OnToggleDateFilter();
        row3.Controls.Add(_dateFilterBox);
        _dateFrom = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110, Enabled = false };
        _dateTo   = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110, Enabled = false };
        // 기본값: from = 30일 전, to = 오늘.
        _dateFrom.Value = DateTime.Today.AddDays(-30);
        _dateTo.Value   = DateTime.Today;
        row3.Controls.Add(_dateFrom);
        row3.Controls.Add(new Label { Text = " ~ ", AutoSize = true, Padding = new Padding(4, 4, 4, 0) });
        row3.Controls.Add(_dateTo);
        row3.Controls.Add(new Label { Text = "(적재 시각 기준, 양끝 포함)", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(8, 4, 0, 0) });
        top.Controls.Add(row3, 0, 2);

        root.Controls.Add(top, 0, 0);

        // ── 결과 ListView (체크박스) ──────────────────────────────────
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            HideSelection = false,
            GridLines = false,
            Font = new Font("맑은 고딕", 9),
        };
        _list.Columns.Add("ID",       50);
        _list.Columns.Add("폴더",    280);
        _list.Columns.Add("파일명",  240);
        _list.Columns.Add("확장자",   60);
        _list.Columns.Add("크기",     90, HorizontalAlignment.Right);
        _list.Columns.Add("적재일",  135);
        _list.Columns.Add("상태",     70);
        _list.ItemChecked += (_, _) => UpdateSelInfo();
        root.Controls.Add(_list, 0, 1);

        // ── 로그 ──────────────────────────────────────────────────────
        _log = new LogPane { Dock = DockStyle.Fill };
        var logFrame = new GroupBox { Text = "로그", Dock = DockStyle.Fill, Height = 90, Padding = new Padding(4) };
        logFrame.Controls.Add(_log);
        root.Controls.Add(logFrame, 0, 2);

        // ── 하단 액션 (Dock=Bottom — 잘림 방지) ──────────────────────
        var bot = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Padding = new Padding(8, 4, 8, 4),
        };
        bot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _selInfoLabel = new Label
        {
            Text = "0건 선택됨", AutoSize = true, ForeColor = Color.Gray,
            Padding = new Padding(0, 8, 0, 0), Font = new Font("맑은 고딕", 9),
        };
        bot.Controls.Add(_selInfoLabel, 0, 0);

        var btnFlow = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _exportBtn     = new Button { Text = "선택 행 CSV 추출", AutoSize = true, Enabled = false };
        _selectNoneBtn = new Button { Text = "전체 해제", AutoSize = true, Enabled = false };
        _selectAllBtn  = new Button { Text = "전체 선택", AutoSize = true, Enabled = false };
        _exportBtn.Click     += (_, _) => ExportSelected();
        _selectNoneBtn.Click += (_, _) => SetAllChecked(false);
        _selectAllBtn.Click  += (_, _) => SetAllChecked(true);
        btnFlow.Controls.Add(_exportBtn);
        btnFlow.Controls.Add(_selectNoneBtn);
        btnFlow.Controls.Add(_selectAllBtn);
        bot.Controls.Add(btnFlow, 1, 0);

        Controls.Add(root);
        Controls.Add(bot);

        try { _repo.EnsureDatabase(); }
        catch (Exception ex) { _log.AppendLine($"[DB 경고] {ex.Message}"); }
    }

    private SearchTarget GetTarget()
        => _targetTitle.Checked ? SearchTarget.Title
         : _targetBody.Checked  ? SearchTarget.Body
         : SearchTarget.Both;

    private SearchMode GetMode()
        => _modeOr.Checked     ? SearchMode.Or
         : _modePhrase.Checked ? SearchMode.Phrase
         : SearchMode.And;

    private void OnToggleDateFilter()
    {
        _dateFrom.Enabled = _dateFilterBox.Checked;
        _dateTo.Enabled   = _dateFilterBox.Checked;
    }

    private async Task DoSearchAsync()
    {
        _searchBtn.Enabled = false;
        _statusLabel.Text = "검색 중...";
        try
        {
            var kw = _keywordBox.Text.Trim();
            var target = GetTarget();
            var mode = GetMode();
            var includeExcluded = _includeExcludedBox.Checked;

            // 적재일 범위 — from 은 그날 00:00, to 는 다음날 00:00(반열림)으로 그날 전체 포함.
            DateTime? pf = null, pt = null;
            if (_dateFilterBox.Checked)
            {
                pf = _dateFrom.Value.Date;
                pt = _dateTo.Value.Date.AddDays(1);
                if (pf > pt)
                {
                    MessageBox.Show(this, "적재일 시작이 종료보다 뒤입니다.", "날짜 범위");
                    _statusLabel.Text = "";
                    return;
                }
            }

            var rows = await Task.Run(() => _search.SearchForExport(
                kw, target, mode, includeExcluded,
                idMin: null, idMax: null, parsedFrom: pf, parsedTo: pt));

            FillList(rows);

            var dateLabel = _dateFilterBox.Checked
                ? $" [적재일 {_dateFrom.Value:yyyy-MM-dd}~{_dateTo.Value:yyyy-MM-dd}]"
                : "";
            _log.AppendLine($"[검색] '{kw}'{dateLabel} → {rows.Count:N0}건");
            _statusLabel.Text = $"{rows.Count:N0}건";
            if (rows.Count >= 200_000)
                _log.AppendLine("  ⚠ 안전 상한(200,000건) 도달 — 조건을 좁혀 다시 검색하세요.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "검색 오류");
            _statusLabel.Text = "오류";
            _log.AppendLine($"[오류] {ex.Message}");
        }
        finally
        {
            _searchBtn.Enabled = true;
        }
    }

    private void FillList(IReadOnlyList<ExportRow> rows)
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var r in rows)
            {
                var sizeStr = r.FileSize >= 1024 * 1024
                    ? $"{r.FileSize / 1024.0 / 1024.0:F1} MB"
                    : $"{r.FileSize / 1024.0:F0} KB";
                var parsedStr = r.ParsedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                var item = new ListViewItem(new[]
                {
                    r.Id.ToString(), r.Directory, r.Filename, r.Extension,
                    sizeStr, parsedStr, r.ParseStatus,
                })
                {
                    Tag = r,
                };
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }
        var any = _list.Items.Count > 0;
        _selectAllBtn.Enabled = any;
        _selectNoneBtn.Enabled = any;
        UpdateSelInfo();
    }

    private void SetAllChecked(bool value)
    {
        _list.BeginUpdate();
        try { foreach (ListViewItem it in _list.Items) it.Checked = value; }
        finally { _list.EndUpdate(); }
        UpdateSelInfo();
    }

    private void UpdateSelInfo()
    {
        var n = _list.CheckedItems.Count;
        _selInfoLabel.Text = $"{n:N0}건 선택됨 (전체 {_list.Items.Count:N0}건)";
        _exportBtn.Enabled = n > 0;
    }

    private void ExportSelected()
    {
        var rows = _list.CheckedItems.Cast<ListViewItem>()
            .Select(i => (ExportRow)i.Tag!)
            .ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "추출할 행을 하나 이상 선택하세요.", "선택 없음");
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "DB 추출 결과 CSV 저장",
            FileName = $"db_export_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultExt = "csv",
            Filter = "CSV (*.csv)|*.csv|CSV (*.csd)|*.csd|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            SearchService.WriteExportCsv(rows, dlg.FileName);
            _log.AppendLine($"[추출] {rows.Count:N0}건 → {dlg.FileName}");
            MessageBox.Show(this, $"{rows.Count:N0}건을 추출했습니다.\n{dlg.FileName}", "추출 완료");
        }
        catch (Exception ex)
        {
            _log.AppendLine($"[추출 실패] {ex.Message}");
            MessageBox.Show(this, ex.Message, "추출 실패");
        }
    }
}
