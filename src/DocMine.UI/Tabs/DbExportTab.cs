// DbExportTab — ⑤ 반출.
//
// DB 레코드를 본문(body_text) 포함 CSV 로 내보내 다른 환경('반입')에 그대로 적재.
//
// 두 가지 흐름:
//   ㆍ 검색 모드(매니페스트 미로드): 키워드/적재일로 검색 → 선택 → 반출.
//   ㆍ 대조 모드(매니페스트 로드):  현재 env 전체 ∪ 매니페스트 합집합을 두 컬럼
//      (현재환경/매니페스트)으로 보여줌 → 필터/선택 → 반출.
//      (어느 쪽이 부분집합인지 무관 — 신규 내보내기·중복 식별 모두 한 뷰로.)
//
// 목록 UI 는 반입과 공유하는 EnvCompareList. 본문은 목록에 안 싣고(메타만), 반출
// 시점에 선택분만 DB 에서 끌어온다(LONGTEXT 대량 로딩 회피).

using DocMine.Core.Config;
using DocMine.Core.Db;
using DocMine.Core.Pipeline;

namespace DocMine.UI.Tabs;

public sealed class DbExportTab : TabPage
{
    private SearchService _search;
    private DocumentRepository _repo;

    // 매니페스트(환경2 적재현황 등 외부 대조본).
    private List<(string Dir, string Fn)> _manifestRaw = new();   // 유령 행 원본 표기용
    private bool _manifestLoaded;
    private readonly ToolTip _tip = new() { AutoPopDelay = 12000, InitialDelay = 400, ReshowDelay = 100 };

    private readonly TextBox _keywordBox;
    private readonly RadioButton _targetBoth, _targetTitle, _targetBody;
    private readonly RadioButton _modeAnd, _modeOr, _modePhrase;
    private readonly CheckBox _includeExcludedBox;
    private readonly CheckBox _dateFilterBox;
    private readonly DateTimePicker _dateFrom, _dateTo;
    private readonly Button _searchBtn;
    private readonly Label _statusLabel;
    private Control[] _searchControls = Array.Empty<Control>();

    private readonly TextBox _manifestBox;
    private readonly Label _manifestStatus;
    private readonly CheckBox _matchNameOnlyBox;

    private IReadOnlyList<ExportRow> _lastResults = Array.Empty<ExportRow>();   // 검색 모드 결과
    private IReadOnlyList<ExportRow> _fullEnv = Array.Empty<ExportRow>();       // 대조 모드 base(전체 env)

    private readonly EnvCompareList _cmp;
    private readonly LogPane _log;
    private readonly Button _exportBtn, _manifestExportBtn;

    public DbExportTab() : base("⑤ 반출")
    {
        var cfg = AppConfig.Current;
        _search = new SearchService(cfg);
        _repo = new DocumentRepository(cfg);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ── 1. 검색 조건 ──────────────────────────────────────────────
        var searchGroup = new GroupBox { Text = "1. 검색 조건", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var searchInner = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 3 };
        searchInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

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
        searchInner.Controls.Add(row1, 0, 0);

        var row2 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        row2.Controls.Add(new Label { Text = "검색 대상:", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        _targetBoth  = new RadioButton { Text = "제목+본문", Checked = true, AutoSize = true };
        _targetTitle = new RadioButton { Text = "제목만",    AutoSize = true };
        _targetBody  = new RadioButton { Text = "본문만",    AutoSize = true };
        row2.Controls.Add(_targetBoth); row2.Controls.Add(_targetTitle); row2.Controls.Add(_targetBody);
        row2.Controls.Add(new Label { Text = "  |  ", AutoSize = true, ForeColor = Color.LightGray, Padding = new Padding(0, 4, 0, 0) });
        row2.Controls.Add(new Label { Text = "방식:", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        _modeAnd    = new RadioButton { Text = "AND", Checked = true, AutoSize = true };
        _modeOr     = new RadioButton { Text = "OR",  AutoSize = true };
        _modePhrase = new RadioButton { Text = "전체 문자열", AutoSize = true };
        row2.Controls.Add(_modeAnd); row2.Controls.Add(_modeOr); row2.Controls.Add(_modePhrase);
        row2.Controls.Add(new Label { Text = "  |  ", AutoSize = true, ForeColor = Color.LightGray, Padding = new Padding(0, 4, 0, 0) });
        _includeExcludedBox = new CheckBox { Text = "제외 항목 포함", Checked = true, AutoSize = true };
        row2.Controls.Add(_includeExcludedBox);
        searchInner.Controls.Add(row2, 0, 1);

        var row3 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        _dateFilterBox = new CheckBox { Text = "적재일 범위", AutoSize = true, Padding = new Padding(0, 4, 4, 0) };
        _dateFilterBox.CheckedChanged += (_, _) => OnToggleDateFilter();
        row3.Controls.Add(_dateFilterBox);
        _dateFrom = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110, Enabled = false };
        _dateTo   = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110, Enabled = false };
        _dateFrom.Value = DateTime.Today.AddDays(-30);
        _dateTo.Value   = DateTime.Today;
        row3.Controls.Add(_dateFrom);
        row3.Controls.Add(new Label { Text = " ~ ", AutoSize = true, Padding = new Padding(4, 4, 4, 0) });
        row3.Controls.Add(_dateTo);
        row3.Controls.Add(new Label { Text = "(적재 시각 기준, 양끝 포함)", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(8, 4, 0, 0) });
        searchInner.Controls.Add(row3, 0, 2);
        searchGroup.Controls.Add(searchInner);
        top.Controls.Add(searchGroup, 0, 0);

        // ── 2. 환경 대조 (선택) ───────────────────────────────────────
        var matchGroup = new GroupBox { Text = "2. 환경 대조 (선택)", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var matchInner = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 3 };
        matchInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var loadRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        loadRow.Controls.Add(new Label { Text = "대조 CSV(매니페스트):", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _manifestBox = new TextBox { Width = 260 };
        loadRow.Controls.Add(_manifestBox);
        var manifestBrowse = new Button { Text = "찾아보기…", AutoSize = true, Margin = new Padding(4, 2, 0, 0) };
        manifestBrowse.Click += (_, _) => BrowseManifest();
        loadRow.Controls.Add(manifestBrowse);
        var manifestLoad = new Button { Text = "불러오기", AutoSize = true, Margin = new Padding(4, 2, 0, 0) };
        manifestLoad.Click += async (_, _) => await LoadManifestAsync();
        loadRow.Controls.Add(manifestLoad);
        var manifestUnload = new Button { Text = "해제", AutoSize = true, Margin = new Padding(4, 2, 0, 0) };
        manifestUnload.Click += (_, _) => UnloadManifest();
        loadRow.Controls.Add(manifestUnload);
        _manifestStatus = new Label { Text = "(미로드)", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(8, 6, 0, 0) };
        loadRow.Controls.Add(_manifestStatus);
        matchInner.Controls.Add(loadRow, 0, 0);

        var toggleRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 2, 0, 0) };
        _matchNameOnlyBox = new CheckBox { Text = "파일명만 일치", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        _matchNameOnlyBox.CheckedChanged += (_, _) => { if (_manifestLoaded) BuildCompareMode(); };
        toggleRow.Controls.Add(_matchNameOnlyBox);
        matchInner.Controls.Add(toggleRow, 0, 1);

        var genRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        genRow.Controls.Add(new Label { Text = "현재 DB → manifest:", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(0, 6, 4, 0) });
        _manifestExportBtn = new Button { Text = "manifest 내보내기", AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        _manifestExportBtn.Click += async (_, _) => await ExportManifestAsync();
        genRow.Controls.Add(_manifestExportBtn);
        matchInner.Controls.Add(genRow, 0, 2);
        matchGroup.Controls.Add(matchInner);
        top.Controls.Add(matchGroup, 0, 1);

        _tip.SetToolTip(_manifestBox,
            "다른 환경에서 'manifest 내보내기'로 만든 CSV(폴더+파일명).\n불러오면 현재 env 전체와 합집합으로 두 컬럼(현재환경/매니페스트) 대조.");
        _tip.SetToolTip(_matchNameOnlyBox,
            "대조 시 폴더를 무시하고 파일명만 비교합니다.\n폴더가 바뀌었어도 같은 파일명이면 일치로 간주.");
        _tip.SetToolTip(_manifestExportBtn,
            "현재 연결된 DB 전체의 (폴더+파일명)을 manifest CSV 로 내보냅니다.\n상대 환경에서 대조에 사용.");

        root.Controls.Add(top, 0, 0);

        // ── 합집합 대조 리스트 (반입과 공유) ──────────────────────────
        _cmp = new EnvCompareList { Dock = DockStyle.Fill };
        _cmp.Configure("현재환경", "매니페스트",
            new EnvCompareList.ColumnDef("ID", 50, HorizontalAlignment.Left),
            new EnvCompareList.ColumnDef("폴더", 280, HorizontalAlignment.Left),
            new EnvCompareList.ColumnDef("파일명", 240, HorizontalAlignment.Left),
            new EnvCompareList.ColumnDef("확장자", 60, HorizontalAlignment.Left),
            new EnvCompareList.ColumnDef("크기", 90, HorizontalAlignment.Right),
            new EnvCompareList.ColumnDef("적재일", 135, HorizontalAlignment.Left),
            new EnvCompareList.ColumnDef("상태", 70, HorizontalAlignment.Left));
        root.Controls.Add(_cmp, 0, 1);

        // ── 로그 ──────────────────────────────────────────────────────
        _log = new LogPane { Dock = DockStyle.Fill };
        var logFrame = new GroupBox { Text = "로그", Dock = DockStyle.Fill, Height = 90, Padding = new Padding(4) };
        logFrame.Controls.Add(_log);
        root.Controls.Add(logFrame, 0, 2);

        // ── 하단 액션 (Dock=Bottom — 잘림 방지) ──────────────────────
        var bot = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 1, Padding = new Padding(8, 4, 8, 4),
        };
        bot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var btnFlow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _exportBtn = new Button { Text = "선택 항목 반출 (본문 포함)", AutoSize = true, Enabled = false };
        _exportBtn.Click += async (_, _) => await ExportSelectedAsync();
        _cmp.SelectionChanged += () => _exportBtn.Enabled = _cmp.SelectedCount > 0;
        btnFlow.Controls.Add(_exportBtn);
        bot.Controls.Add(btnFlow, 0, 0);

        Controls.Add(root);
        Controls.Add(bot);

        _searchControls = new Control[]
        {
            _keywordBox, _searchBtn, _targetBoth, _targetTitle, _targetBody,
            _modeAnd, _modeOr, _modePhrase, _includeExcludedBox, _dateFilterBox, _dateFrom, _dateTo,
        };

        try { _repo.EnsureDatabase(); }
        catch (Exception ex) { _log.AppendLine($"[DB 경고] {ex.Message}"); }

        VisibleChanged += (_, _) => { if (Visible) RefreshServices(); };
    }

    private void RefreshServices()
    {
        var cfg = AppConfig.Current;
        _search = new SearchService(cfg);
        _repo   = new DocumentRepository(cfg);
    }

    private SearchTarget GetTarget()
        => _targetTitle.Checked ? SearchTarget.Title : _targetBody.Checked ? SearchTarget.Body : SearchTarget.Both;

    private SearchMode GetMode()
        => _modeOr.Checked ? SearchMode.Or : _modePhrase.Checked ? SearchMode.Phrase : SearchMode.And;

    private void OnToggleDateFilter()
    {
        _dateFrom.Enabled = _dateFilterBox.Checked;
        _dateTo.Enabled   = _dateFilterBox.Checked;
    }

    // ─ 검색 모드 ──────────────────────────────────────────────────────
    private async Task DoSearchAsync()
    {
        if (_manifestLoaded) return;   // 대조 모드에선 검색 비활성
        _searchBtn.Enabled = false;
        _statusLabel.Text = "검색 중...";
        try
        {
            var kw = _keywordBox.Text.Trim();
            var target = GetTarget();
            var mode = GetMode();
            var includeExcluded = _includeExcludedBox.Checked;
            DateTime? pf = null, pt = null;
            if (_dateFilterBox.Checked)
            {
                pf = _dateFrom.Value.Date;
                pt = _dateTo.Value.Date.AddDays(1);
                if (pf > pt) { MessageBox.Show(this, "적재일 시작이 종료보다 뒤입니다.", "날짜 범위"); _statusLabel.Text = ""; return; }
            }

            var rows = await Task.Run(() => _search.SearchForExport(
                kw, target, mode, includeExcluded, idMin: null, idMax: null, parsedFrom: pf, parsedTo: pt));

            _lastResults = rows;
            FeedSearchMode();
            var dateLabel = _dateFilterBox.Checked ? $" [적재일 {_dateFrom.Value:yyyy-MM-dd}~{_dateTo.Value:yyyy-MM-dd}]" : "";
            _log.AppendLine($"[검색] '{kw}'{dateLabel} | table={AppConfig.Current.DbTable} → {rows.Count:N0}건");
            if (rows.Count >= 200_000) _log.AppendLine("  ⚠ 안전 상한(200,000건) 도달 — 조건을 좁혀 다시 검색하세요.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "검색 오류");
            _statusLabel.Text = "오류";
            _log.AppendLine($"[오류] {ex.Message}");
        }
        finally { _searchBtn.Enabled = !_manifestLoaded; }
    }

    private void FeedSearchMode()
    {
        var rows = _lastResults.Select(r => RowOf(r, inCompare: false)).ToList();
        _cmp.SetRows(rows, compareLoaded: false);
        _statusLabel.Text = $"{_lastResults.Count:N0}건";
    }

    // ─ 대조 모드 ──────────────────────────────────────────────────────
    private void BuildCompareMode()
    {
        var union = EnvCompare.Build(_fullEnv, r => (r.Directory, r.Filename), _manifestRaw, _matchNameOnlyBox.Checked);
        var rows = new List<CompareRow>(union.Base.Count + union.CompareOnly.Count);
        foreach (var m in union.Base) rows.Add(RowOf(m.Item, m.InCompare));
        foreach (var g in union.CompareOnly) rows.Add(GhostOf(g));
        _cmp.SetRows(rows, compareLoaded: true);

        int total = union.Base.Count;
        int both  = union.Base.Count(m => m.InCompare);
        _statusLabel.Text = $"현재환경 {total:N0} · 양쪽 일치 {both:N0} · 현재환경만 {total - both:N0} · 매니페스트만 {union.CompareOnly.Count:N0}";
    }

    private static CompareRow RowOf(ExportRow r, bool inCompare) => new()
    {
        Key = (r.Directory, r.Filename),
        InBase = true,
        InCompare = inCompare,
        Cells = new[] { r.Id.ToString(), r.Directory, r.Filename, r.Extension, SizeStr(r.FileSize),
                        r.ParsedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "", r.ParseStatus },
        Item = r,
    };

    private static CompareRow GhostOf((string Dir, string Fn) k) => new()
    {
        Key = (k.Dir, k.Fn),
        InBase = false,
        InCompare = true,
        Cells = new[] { "", k.Dir, k.Fn, "", "", "", "" },
        Item = null,
    };

    private static string SizeStr(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB" : $"{bytes / 1024.0:F0} KB";

    private void SetSearchEnabled(bool on)
    {
        foreach (var c in _searchControls) c.Enabled = on;
        if (on) OnToggleDateFilter();   // 날짜 picker 는 체크 상태 따름
    }

    // ─ 매니페스트 ─────────────────────────────────────────────────────
    private void BrowseManifest()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "대조 CSV(매니페스트) 선택",
            Filter = "CSV (*.csv;*.csd)|*.csv;*.csd|모든 파일 (*.*)|*.*",
        };
        if (!string.IsNullOrEmpty(_manifestBox.Text)) dlg.FileName = Path.GetFileName(_manifestBox.Text);
        if (dlg.ShowDialog(this) == DialogResult.OK) _manifestBox.Text = dlg.FileName;
    }

    private async Task LoadManifestAsync()
    {
        var path = _manifestBox.Text.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "대조 CSV(매니페스트) 파일을 선택하세요.", "파일 없음");
            return;
        }
        _statusLabel.Text = "대조 준비 중… (전체 env 로드)";
        try
        {
            var includeExcluded = _includeExcludedBox.Checked;
            var (manifest, fullEnv) = await Task.Run(() =>
            {
                var m = CsvIngestHelpers.LoadManifestRows(path);
                // 대조 base = 현재 env 전체 (키워드 무관). 메타만(본문 제외).
                var env = _search.SearchForExport("", SearchTarget.Both, SearchMode.And, includeExcluded);
                return (m, env);
            });
            _manifestRaw = manifest;
            _fullEnv = fullEnv;
            _manifestLoaded = true;
            _manifestStatus.Text = $"매니페스트 {manifest.Count:N0}건 · 현재환경 {fullEnv.Count:N0}건";
            _manifestStatus.ForeColor = Color.SeaGreen;
            SetSearchEnabled(false);
            BuildCompareMode();
            _log.AppendLine($"[대조] 매니페스트 {manifest.Count:N0}건 로드 — {path}");
        }
        catch (Exception ex)
        {
            _manifestLoaded = false;
            _manifestStatus.Text = "로드 실패";
            _manifestStatus.ForeColor = Color.Firebrick;
            MessageBox.Show(this, ex.Message, "대조 로드 실패");
            _log.AppendLine($"[오류] {ex.Message}");
        }
    }

    private void UnloadManifest()
    {
        if (!_manifestLoaded) return;
        _manifestLoaded = false;
        _manifestRaw = new();
        _fullEnv = Array.Empty<ExportRow>();
        _manifestStatus.Text = "(미로드)";
        _manifestStatus.ForeColor = Color.Gray;
        SetSearchEnabled(true);
        FeedSearchMode();   // 마지막 검색 결과로 복귀
        _log.AppendLine("[대조] 해제됨 — 검색 모드");
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
        var prev = _statusLabel.Text;
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
        finally { _manifestExportBtn.Enabled = true; _statusLabel.Text = prev; }
    }

    // ─ 반출 (본문 포함) ──────────────────────────────────────────────
    private async Task ExportSelectedAsync()
    {
        var ids = _cmp.SelectedItems.OfType<ExportRow>().Select(r => r.Id).ToList();
        if (ids.Count == 0) { MessageBox.Show(this, "반출할 행을 하나 이상 선택하세요.", "선택 없음"); return; }

        using var dlg = new SaveFileDialog
        {
            Title = "반출 CSV 저장 (본문 포함 — 상대 환경 반입용)",
            FileName = $"db_export_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultExt = "csv",
            Filter = "CSV (*.csv)|*.csv|CSV (*.csd)|*.csd|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var path = dlg.FileName;

        _exportBtn.Enabled = false;
        var prev = _statusLabel.Text;
        _statusLabel.Text = "반출 중… (본문 조회)";
        try
        {
            var n = await Task.Run(() =>
            {
                var records = _repo.LoadRecordsByIds(ids);
                DocTransferCsv.Write(records, path);
                return records.Count;
            });
            _statusLabel.Text = prev;
            _log.AppendLine($"[반출] {n:N0}건 (본문 포함) → {path}");
            MessageBox.Show(this, $"{n:N0}건을 본문 포함으로 반출했습니다.\n{path}", "반출 완료");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = prev;
            _log.AppendLine($"[반출 실패] {ex.Message}");
            MessageBox.Show(this, ex.Message, "반출 실패");
        }
        finally { _exportBtn.Enabled = _cmp.SelectedCount > 0; }
    }
}
