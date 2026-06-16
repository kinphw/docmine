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
using DocMine.Core.Pipeline;

namespace DocMine.UI.Tabs;

public sealed class DbExportTab : TabPage
{
    // readonly 아님 — ⑥ 설정에서 DB/테이블 변경 시 탭 활성화마다 현재 설정으로 갱신.
    private SearchService _search;
    private DocumentRepository _repo;

    // ListView 컬럼 — '환경2 적재'를 ID 다음에 삽입.
    private const int ColId = 0, ColArchived = 1, ColDir = 2, ColFile = 3,
                      ColExt = 4, ColSize = 5, ColParsedAt = 6, ColStatus = 7;

    // 환경2 적재 현황(manifest) — NormKey 집합. null = 미로드.
    private HashSet<(string, string)>? _manifest;
    private bool _suppressItemCheck;   // SetAllChecked 중 ItemCheck 재진입 차단

    private readonly TextBox _keywordBox;
    private readonly RadioButton _targetBoth, _targetTitle, _targetBody;
    private readonly RadioButton _modeAnd, _modeOr, _modePhrase;
    private readonly CheckBox _includeExcludedBox;
    private readonly CheckBox _dateFilterBox;
    private readonly DateTimePicker _dateFrom, _dateTo;
    private readonly Button _searchBtn;
    private readonly Label _statusLabel;

    // 기적재 현황 대조
    private readonly TextBox _manifestBox;
    private readonly Label _manifestStatus;
    private readonly CheckBox _lockArchivedBox;
    private readonly CheckBox _excludeArchivedBox;   // 신규만 — 기적재 행을 결과에서 숨김

    // 마지막 검색 결과 원본 — 토글/manifest 로드 시 재검색 없이 재필터링.
    private IReadOnlyList<ExportRow> _lastResults = Array.Empty<ExportRow>();

    private readonly ListView _list;
    private ColumnHeader _archivedCol = null!;   // 환경2 적재 컬럼 — manifest 로드 시 표시
    private readonly LogPane _log;

    private readonly Button _selectAllBtn, _selectNoneBtn, _exportBtn, _manifestExportBtn;
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
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 4 };
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

        // row4: 기적재 현황(환경2 manifest) 대조.
        var row4 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        row4.Controls.Add(new Label { Text = "기적재 현황 CSV:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _manifestBox = new TextBox { Width = 300 };
        row4.Controls.Add(_manifestBox);
        var manifestBrowse = new Button { Text = "찾아보기…", AutoSize = true, Margin = new Padding(4, 2, 0, 0) };
        manifestBrowse.Click += (_, _) => BrowseManifest();
        row4.Controls.Add(manifestBrowse);
        var manifestLoad = new Button { Text = "불러오기", AutoSize = true, Margin = new Padding(4, 2, 0, 0) };
        manifestLoad.Click += (_, _) => LoadManifest();
        row4.Controls.Add(manifestLoad);
        _manifestStatus = new Label { Text = "(미로드)", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(8, 6, 0, 0) };
        row4.Controls.Add(_manifestStatus);
        _excludeArchivedBox = new CheckBox { Text = "기적재 제외 (신규만)", AutoSize = true, Margin = new Padding(12, 4, 0, 0) };
        _excludeArchivedBox.CheckedChanged += (_, _) => ReapplyView();
        row4.Controls.Add(_excludeArchivedBox);
        _lockArchivedBox = new CheckBox { Text = "기적재 선택 잠금", AutoSize = true, Margin = new Padding(12, 4, 0, 0) };
        _lockArchivedBox.CheckedChanged += (_, _) => OnToggleLock();
        row4.Controls.Add(_lockArchivedBox);
        top.Controls.Add(row4, 0, 3);

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
        // 환경2 적재 — manifest 로드 전엔 width 0(숨김), 로드 시 확장.
        _archivedCol = _list.Columns.Add("환경2", 0, HorizontalAlignment.Center);
        _list.Columns.Add("폴더",    280);
        _list.Columns.Add("파일명",  240);
        _list.Columns.Add("확장자",   60);
        _list.Columns.Add("크기",     90, HorizontalAlignment.Right);
        _list.Columns.Add("적재일",  135);
        _list.Columns.Add("상태",     70);
        _list.ItemCheck   += OnItemCheck;        // 체크 변경 '전' — 잠금 시 거부
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
        // 이 DB(환경2 등) 전체의 폴더/파일명만 manifest CSV 로 덤프 — 검색과 무관.
        _manifestExportBtn = new Button { Text = "적재현황 내보내기", AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
        _exportBtn.Click     += (_, _) => ExportSelected();
        _selectNoneBtn.Click += (_, _) => SetAllChecked(false);
        _selectAllBtn.Click  += (_, _) => SetAllChecked(true);
        _manifestExportBtn.Click += async (_, _) => await ExportManifestAsync();
        // RightToLeft FlowDirection — Add 순서 역순으로 화면에 배치.
        btnFlow.Controls.Add(_exportBtn);
        btnFlow.Controls.Add(_selectNoneBtn);
        btnFlow.Controls.Add(_selectAllBtn);
        btnFlow.Controls.Add(_manifestExportBtn);
        bot.Controls.Add(btnFlow, 1, 0);

        Controls.Add(root);
        Controls.Add(bot);

        try { _repo.EnsureDatabase(); }
        catch (Exception ex) { _log.AppendLine($"[DB 경고] {ex.Message}"); }

        // ⑥ 설정에서 DB/테이블을 바꿔도 반영되도록 탭 활성화마다 현재 설정으로 갱신.
        VisibleChanged += (_, _) => { if (Visible) RefreshServices(); };
    }

    private void RefreshServices()
    {
        var cfg = AppConfig.Current;
        _search = new SearchService(cfg);
        _repo   = new DocumentRepository(cfg);
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

            _lastResults = rows;
            FillList(rows);

            var dateLabel = _dateFilterBox.Checked
                ? $" [적재일 {_dateFrom.Value:yyyy-MM-dd}~{_dateTo.Value:yyyy-MM-dd}]"
                : "";
            _log.AppendLine($"[검색] '{kw}'{dateLabel} | table={AppConfig.Current.DbTable} → {rows.Count:N0}건");
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
        // '기적재 제외(신규만)' ON + manifest 로드됨 → 환경2 에 있는 행은 숨김.
        var excludeArchived = _excludeArchivedBox.Checked && _manifest is not null;
        var hidden = 0;

        // 아이템을 먼저 다 만든 뒤 AddRange 로 일괄 투입 — 개별 Items.Add 루프는
        // 대량(수천~수만)에서 매번 내부 재계산이 일어나 매우 느리다.
        var items = new List<ListViewItem>(rows.Count);
        foreach (var r in rows)
        {
            var archived = IsArchived(r);
            if (excludeArchived && archived) { hidden++; continue; }
            var sizeStr = r.FileSize >= 1024 * 1024
                ? $"{r.FileSize / 1024.0 / 1024.0:F1} MB"
                : $"{r.FileSize / 1024.0:F0} KB";
            var parsedStr = r.ParsedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
            // subitem 순서 = 컬럼 순서 (ID, 환경2, 폴더, 파일명, 확장자, 크기, 적재일, 상태).
            items.Add(new ListViewItem(new[]
            {
                r.Id.ToString(), archived ? "✓" : "", r.Directory, r.Filename, r.Extension,
                sizeStr, parsedStr, r.ParseStatus,
            })
            {
                Tag = r,
            });
        }

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _list.Items.AddRange(items.ToArray());
        }
        finally
        {
            _list.EndUpdate();
        }
        if (excludeArchived && hidden > 0)
            _log.AppendLine($"  기적재 {hidden:N0}건 제외 — 신규 {_list.Items.Count:N0}건 표시");

        var any = _list.Items.Count > 0;
        _selectAllBtn.Enabled = any;
        _selectNoneBtn.Enabled = any;
        UpdateSelInfo();
    }

    // 토글/manifest 로드 시 마지막 검색 결과를 현재 뷰 설정으로 재구성 (재검색 없음).
    private void ReapplyView() => FillList(_lastResults);

    private void SetAllChecked(bool value)
    {
        // 잠금 ON 이면 '전체 선택' 은 신규(미적재)만 체크. ItemCheck 재진입은 suppress 로 차단.
        _suppressItemCheck = true;
        _list.BeginUpdate();
        try
        {
            foreach (ListViewItem it in _list.Items)
            {
                var allow = value && !(_lockArchivedBox.Checked && IsArchived((ExportRow)it.Tag!));
                it.Checked = allow;
            }
        }
        finally { _list.EndUpdate(); _suppressItemCheck = false; }
        UpdateSelInfo();
    }

    // ─ 기적재(환경2) 대조 ─────────────────────────────────────────────
    private bool IsArchived(ExportRow r)
        => _manifest is not null && _manifest.Contains(CsvIngestHelpers.NormKey(r.Directory, r.Filename));

    private void OnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_suppressItemCheck) return;
        // 잠금 ON 상태에서 기적재 행을 체크하려 하면 거부.
        if (e.NewValue == CheckState.Checked && _lockArchivedBox.Checked
            && IsArchived((ExportRow)_list.Items[e.Index].Tag!))
        {
            e.NewValue = CheckState.Unchecked;
        }
    }

    private void OnToggleLock()
    {
        // 잠금을 켜는 순간 이미 체크돼 있던 기적재 행을 해제.
        if (_lockArchivedBox.Checked)
        {
            _suppressItemCheck = true;
            _list.BeginUpdate();
            try
            {
                foreach (ListViewItem it in _list.Items)
                    if (it.Checked && IsArchived((ExportRow)it.Tag!)) it.Checked = false;
            }
            finally { _list.EndUpdate(); _suppressItemCheck = false; }
            UpdateSelInfo();
        }
    }

    private void BrowseManifest()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "기적재 현황 CSV 선택 (환경2에서 추출)",
            Filter = "CSV (*.csv;*.csd)|*.csv;*.csd|모든 파일 (*.*)|*.*",
        };
        if (!string.IsNullOrEmpty(_manifestBox.Text))
            dlg.FileName = Path.GetFileName(_manifestBox.Text);
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _manifestBox.Text = dlg.FileName;
    }

    private void LoadManifest()
    {
        var path = _manifestBox.Text.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "기적재 현황 CSV 파일을 선택하세요.", "파일 없음");
            return;
        }
        try
        {
            _manifest = CsvIngestHelpers.LoadManifestKeys(path);
            _manifestStatus.Text = $"기적재 {_manifest.Count:N0}건 로드됨";
            _manifestStatus.ForeColor = Color.SeaGreen;
            _archivedCol.Width = 60;
            ReapplyView();   // '환경2' 컬럼 채움 + (제외 토글 시) 신규만 필터
            _log.AppendLine($"[기적재 현황] {_manifest.Count:N0}건 로드 — {path}");
        }
        catch (Exception ex)
        {
            _manifest = null;
            _manifestStatus.Text = "로드 실패";
            _manifestStatus.ForeColor = Color.Firebrick;
            MessageBox.Show(this, ex.Message, "기적재 현황 로드 실패");
        }
    }

    private async Task ExportManifestAsync()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "적재 현황 manifest 저장 (폴더/파일명만)",
            FileName = $"manifest_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultExt = "csv",
            Filter = "CSV (*.csv)|*.csv|CSV (*.csd)|*.csd|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _manifestExportBtn.Enabled = false;
        _statusLabel.Text = "적재현황 추출 중...";
        try
        {
            var path = dlg.FileName;
            var n = await Task.Run(() => _repo.ExportManifestCsv(path));
            _log.AppendLine($"[적재현황] {n:N0}건 → {path}");
            MessageBox.Show(this, $"{n:N0}건의 폴더/파일명을 추출했습니다.\n{path}", "현황 추출 완료");
        }
        catch (Exception ex)
        {
            _log.AppendLine($"[적재현황 실패] {ex.Message}");
            MessageBox.Show(this, ex.Message, "현황 추출 실패");
        }
        finally
        {
            _manifestExportBtn.Enabled = true;
            _statusLabel.Text = "";
        }
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
