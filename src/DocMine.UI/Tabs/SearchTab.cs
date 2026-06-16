// SearchTab — Python search_gui.py 의 App 클래스 등가.
//
// 핵심 기능 (Python 판과 동일):
//   - 키워드 검색 (제목/본문/제목+본문 × AND/OR/Phrase)
//   - 제외 항목 포함 토글
//   - ID 범위 필터
//   - 페이징 (200건 단위) + "더보기" / "전체 조회"
//   - hover tooltip (키워드 빨강 강조) — Timer 500ms 정지 후 표시
//   - 우클릭 메뉴 (필드별 복사 + 파일/경로 열기 + 제외/완전삭제)
//   - Ctrl+C → CF_HDROP 클립보드 (Explorer 에서 Ctrl+V 로 파일 복사)
//   - 드래그 = DoDragDrop(FileDrop)
//   - 더블클릭 = 파일 열기, Del = 제외
//
// 1차 보고 회귀 수정 메모:
//   - hover: MouseMove 마다 즉시 tooltip 이 아니라 Timer 기반 (정지 500ms 후).
//     MouseWheel 발생 시 tooltip 즉시 hide — 스크롤 후 잘못된 셀에 박혀있는 문제 차단.
//   - 제외 행 표시: ForeColor 회색 → Font.Italic. 선택 시 highlight 와 충돌 없음.
//     Python 판은 tk.Treeview 의 tag 컬러라 .NET ListView 동작과 다른 모델.
//   - Ctrl+C / Del: ListView.KeyDown 대신 ProcessCmdKey override —
//     우클릭 컨텍스트 메뉴 닫힘 등으로 ListView focus 가 풀려도 항상 잡힘.
//   - ToolTipForm: owner 없이 top-level Form 으로 떠서 z-order/paint 충돌 차단.

using System.Diagnostics;
using DocMine.Core.Config;
using DocMine.Core.Db;
using DocMine.Win32;

namespace DocMine.UI.Tabs;

public sealed class SearchTab : TabPage
{
    private sealed record FullRow(
        int Id, string Directory, string Filename,
        string PreviewFull, string ParsedAtStr, bool IsExcluded);

    // readonly 아님 — ⑥ 설정에서 DB/테이블 변경 시 탭 활성화마다 현재 설정으로 갱신.
    private SearchService _search;
    private DocumentRepository _repo;

    // 검색 컨트롤
    private readonly TextBox _keywordBox;
    private readonly Button _searchBtn;
    private readonly Label _statusLabel;
    private readonly RadioButton _targetBoth, _targetTitle, _targetBody;
    private readonly RadioButton _modeAnd, _modeOr, _modePhrase;
    private readonly CheckBox _includeExcludedBox;
    private readonly CheckBox _idFilterBox;
    private readonly TextBox _idMinBox, _idMaxBox;
    private readonly CheckBox _showParsedAtBox;   // 우상단 — 적재일 컬럼 표시 토글

    // ListView 컬럼 인덱스 — 한 곳에서 관리 (적재일 컬럼 삽입으로 미리보기가 밀림).
    private const int ColId = 0, ColDir = 1, ColFile = 2, ColParsedAt = 3, ColPreview = 4;

    // 결과 ListView + 로그
    private readonly ListView _list;
    private ColumnHeader _parsedAtCol = null!;    // 적재일 컬럼 — width 토글로 표시/숨김
    private readonly LogPane _log;

    // 하단 버튼 (페이징 제거 — VirtualMode 로 전체를 한 번에 표시)
    private readonly Button _delBtn, _openBtn, _openPathBtn;
    private readonly Label _infoLabel;
    private const string InfoDefault = "더블클릭: 파일 열기  |  드래그/Ctrl+C: 탐색기로 복사  |  Del: 검색에서 제외";

    // 가상화 데이터 — 검색 결과 전체를 메모리에 보유, ListView 는 보이는 행만 그림.
    private readonly List<FullRow> _rows = new();
    private int _total;
    // hover 스니펫 키워드 강조용으로만 마지막 검색어/방식 유지.
    private string _lastKw = "";
    private SearchMode _lastMode = SearchMode.And;

    // hover tooltip — Timer 기반 (마우스 정지 후 표시).
    private readonly System.Windows.Forms.Timer _hoverTimer;
    private Point _lastHoverPos;
    private ToolTipForm? _tooltip;

    // 제외 행 표시용 italic 폰트. ForeColor 안 건드려 selection highlight 와 충돌 안 함.
    private readonly Font _italicFont;

    public SearchTab() : base("③ 검색 (HWP+PDF)")
    {
        var cfg = AppConfig.Current;
        _search = new SearchService(cfg);
        _repo = new DocumentRepository(cfg);

        _italicFont = new Font("맑은 고딕", 9, FontStyle.Italic);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 검색 컨트롤
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 결과
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 로그
        // 하단 버튼은 root 밖에서 Dock = Bottom 으로 — TableLayoutPanel 의 AutoSize row
        // 안에 또 AutoSize TableLayoutPanel 을 박으면 desired-height 체인이 0 으로
        // collapse 되는 케이스가 있어, 절대 잘리지 않는 Dock=Bottom 패턴을 쓴다.

        // ─ 상단 검색 컨트롤 ────────────────────────────────────────────
        // 2열: 0열 = 검색 컨트롤(row1/row2), 1열 = 우상단 '적재일 표시' 체크박스.
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

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

        var row2 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        row2.Controls.Add(new Label { Text = "검색 대상:", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        _targetBoth  = new RadioButton { Text = "제목+본문", Checked = true, AutoSize = true };
        _targetTitle = new RadioButton { Text = "제목만",    AutoSize = true };
        _targetBody  = new RadioButton { Text = "본문만",    AutoSize = true };
        row2.Controls.Add(_targetBoth);
        row2.Controls.Add(_targetTitle);
        row2.Controls.Add(_targetBody);

        row2.Controls.Add(new Label { Text = "  |  ", AutoSize = true, ForeColor = Color.LightGray, Padding = new Padding(0, 4, 0, 0) });
        row2.Controls.Add(new Label { Text = "검색 방식:", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        _modeAnd    = new RadioButton { Text = "AND (공백=모두 포함)", Checked = true, AutoSize = true };
        _modeOr     = new RadioButton { Text = "OR (공백=하나라도)",  AutoSize = true };
        _modePhrase = new RadioButton { Text = "전체 문자열",          AutoSize = true };
        row2.Controls.Add(_modeAnd);
        row2.Controls.Add(_modeOr);
        row2.Controls.Add(_modePhrase);

        row2.Controls.Add(new Label { Text = "  |  ", AutoSize = true, ForeColor = Color.LightGray, Padding = new Padding(0, 4, 0, 0) });
        _includeExcludedBox = new CheckBox { Text = "제외 항목 포함", AutoSize = true };
        _includeExcludedBox.CheckedChanged += async (_, _) => await DoSearchAsync();
        row2.Controls.Add(_includeExcludedBox);

        row2.Controls.Add(new Label { Text = "  |  ID 범위", AutoSize = true, Padding = new Padding(8, 4, 4, 0) });
        _idFilterBox = new CheckBox { AutoSize = true };
        _idFilterBox.CheckedChanged += (_, _) => OnToggleIdFilter();
        row2.Controls.Add(_idFilterBox);
        row2.Controls.Add(new Label { Text = " ID ≥", AutoSize = true, Padding = new Padding(6, 4, 2, 0) });
        _idMinBox = new TextBox { Width = 80, Enabled = false };
        _idMinBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = DoSearchAsync(); } };
        row2.Controls.Add(_idMinBox);
        row2.Controls.Add(new Label { Text = " ID ≤", AutoSize = true, Padding = new Padding(6, 4, 2, 0) });
        _idMaxBox = new TextBox { Width = 80, Enabled = false };
        _idMaxBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = DoSearchAsync(); } };
        row2.Controls.Add(_idMaxBox);

        top.Controls.Add(row2, 0, 1);

        // 우상단 — 적재일(최종 파싱 시각) 컬럼 표시 토글. 기본 비체크.
        _showParsedAtBox = new CheckBox
        {
            Text = "적재일 표시",
            AutoSize = true,
            Checked = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Margin = new Padding(12, 6, 0, 0),
        };
        _showParsedAtBox.CheckedChanged += (_, _) => ToggleParsedAtColumn();
        top.Controls.Add(_showParsedAtBox, 1, 0);

        root.Controls.Add(top, 0, 0);

        // ─ 결과 ListView ──────────────────────────────────────────────
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = true,
            GridLines = false,
            Font = new Font("맑은 고딕", 9),
            AllowDrop = false,  // 드래그 source 만 — drop target 아님
            VirtualMode = true, // 전체 결과를 _rows 에 두고 보이는 행만 on-demand 렌더
        };
        _list.RetrieveVirtualItem += OnRetrieveItem;
        _list.Columns.Add("ID",              50);
        _list.Columns.Add("폴더",           300);
        _list.Columns.Add("파일명",         280);
        // 적재일 — 기본 width 0(숨김). '적재일 표시' 체크 시 ToggleParsedAtColumn 으로 확장.
        _parsedAtCol = _list.Columns.Add("적재일", 0);
        _list.Columns.Add("내용 미리보기",  500);
        _list.DoubleClick += (_, _) => OpenSelectedFile();
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _list.MouseMove  += OnListMouseMove;
        _list.MouseLeave += (_, _) => { _hoverTimer!.Stop(); HideTooltip(); };
        _list.MouseWheel += (_, _) => { _hoverTimer!.Stop(); HideTooltip(); };
        _list.MouseDown  += (_, _) => { _hoverTimer!.Stop(); HideTooltip(); };
        _list.MouseClick += OnListMouseClick;
        _list.ItemDrag   += OnListItemDrag;
        root.Controls.Add(_list, 0, 1);

        // hover Timer — 마우스가 셀 위에서 250ms 동안 정지하면 Tick.
        // OS 기본 ToolTip 은 500ms 정도지만 운영 환경에서 빠른 미리보기 선호.
        _hoverTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _hoverTimer.Tick += OnHoverTimerTick;

        // ─ 로그 ────────────────────────────────────────────────────────
        _log = new LogPane { Dock = DockStyle.Fill, Height = 80 };
        var logFrame = new GroupBox { Text = "로그", Dock = DockStyle.Top, Height = 100, Padding = new Padding(4) };
        logFrame.Controls.Add(_log);
        root.Controls.Add(logFrame, 0, 2);

        // ─ 하단: info label (좌) + 버튼 (우) ─────────────────────────
        //   root 의 마지막 row 가 아니라 폼 직속 Dock=Bottom 으로 박는다.
        //   TableLayoutPanel AutoSize row 안에 또 AutoSize 컨테이너를 박으면
        //   desired-height 가 0 으로 잡히는 윈도우 폼 회귀가 있어, 버튼이
        //   기본 창 크기에선 안 보이고 최대화 시에만 일부 보이는 증상 발생.
        var bot = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1,
            Padding = new Padding(8, 4, 8, 4),
        };
        bot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _infoLabel = new Label
        {
            Text = InfoDefault,
            AutoSize = true,
            ForeColor = Color.Gray,
            Padding = new Padding(0, 6, 0, 0),
            Font = new Font("맑은 고딕", 9),
        };
        bot.Controls.Add(_infoLabel, 0, 0);

        var btnFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _openBtn      = new Button { Text = "파일 열기",  AutoSize = true, Enabled = false };
        _openPathBtn  = new Button { Text = "경로 열기",  AutoSize = true, Enabled = false };
        _delBtn       = new Button { Text = "제외 (Del)", AutoSize = true, Enabled = false };
        _openBtn.Click     += (_, _) => OpenSelectedFile();
        _openPathBtn.Click += (_, _) => OpenSelectedPath();
        _delBtn.Click      += (_, _) => DeleteSelected();
        btnFlow.Controls.Add(_openBtn);
        btnFlow.Controls.Add(_openPathBtn);
        btnFlow.Controls.Add(_delBtn);
        bot.Controls.Add(btnFlow, 1, 0);

        // Dock 처리는 Controls 콜렉션 *역순* — root(Fill) 먼저 add, bot(Bottom) 나중.
        // 역순 처리로 bot 이 먼저 하단을 차지하고 root 가 잔여 영역을 채운다.
        Controls.Add(root);
        Controls.Add(bot);

        _log.AppendLine($"  settings: {cfg.SettingsPath}");
        _log.AppendLine($"  DB:       {cfg.DbUser}@{cfg.DbHost}:{cfg.DbPort}/{cfg.DbName} (table={cfg.DbTable})");
        try { _repo.EnsureDatabase(); }
        catch (Exception ex) { _log.AppendLine($"[DB 경고] {ex.Message}"); }

        // ⑥ 설정에서 DB/테이블을 바꿔도 검색에 반영되도록, 탭이 보여질 때마다
        // 현재 AppConfig 로 서비스 재생성 (기존엔 생성자 1회 캡처라 변경 무시됨).
        VisibleChanged += (_, _) => { if (Visible) RefreshServices(); };
    }

    private void RefreshServices()
    {
        var cfg = AppConfig.Current;
        _search = new SearchService(cfg);
        _repo   = new DocumentRepository(cfg);
    }

    // ─ Ctrl+C / Ctrl+Insert / Del — 폼 단에서 가로채기 ───────────────
    // ListView.KeyDown 이 우클릭 컨텍스트 메뉴 close 등으로 focus 풀린 뒤 잡지 못하는
    // 케이스가 있어, 가장 신뢰성 있는 ProcessCmdKey 패턴 사용.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_list.Focused && _list.SelectedIndices.Count == 0)
            return base.ProcessCmdKey(ref msg, keyData);

        switch (keyData)
        {
            case Keys.Control | Keys.C:
            case Keys.Control | Keys.Insert:
                if (_list.SelectedIndices.Count > 0) { CopySelectedFiles(); return true; }
                break;
            case Keys.Delete:
                if (_list.SelectedIndices.Count > 0) { DeleteSelected(); return true; }
                break;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ─ 검색 ──────────────────────────────────────────────────────────

    private SearchTarget GetTarget()
        => _targetTitle.Checked ? SearchTarget.Title
         : _targetBody.Checked  ? SearchTarget.Body
         : SearchTarget.Both;

    private SearchMode GetMode()
        => _modeOr.Checked     ? SearchMode.Or
         : _modePhrase.Checked ? SearchMode.Phrase
         : SearchMode.And;

    private (int? Min, int? Max) ParseIdFilters()
    {
        if (!_idFilterBox.Checked) return (null, null);
        int? Parse(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return null;
            if (!int.TryParse(s, out var n)) throw new InvalidOperationException($"ID 값이 정수가 아닙니다: {s}");
            return n;
        }
        return (Parse(_idMinBox.Text), Parse(_idMaxBox.Text));
    }

    private async Task DoSearchAsync()
    {
        var kw = _keywordBox.Text.Trim();
        _searchBtn.Enabled = false;
        _statusLabel.Text = "검색 중...";
        try
        {
            var target = GetTarget();
            var mode = GetMode();
            var includeExcluded = _includeExcludedBox.Checked;
            var (idMin, idMax) = ParseIdFilters();
            _lastKw = kw; _lastMode = mode;

            // 선택/표시 초기화 — VirtualMode 라 Items.Clear 대신 VirtualListSize=0.
            _list.SelectedIndices.Clear();
            _rows.Clear();
            _list.VirtualListSize = 0;

            var total = await Task.Run(() => _search.CountResults(kw, target, mode, includeExcluded, idMin, idMax));

            var modeLabel = mode switch { SearchMode.Or => "OR", SearchMode.Phrase => "전체문자열", _ => "AND" };
            var idLabel = (idMin, idMax) switch { (null, null) => "", _ => $" [ID {idMin}~{idMax}]" };
            var exLabel = includeExcluded ? " [제외 포함]" : "";
            _log.AppendLine($"[검색] '{kw}' | 대상: {target} | 방식: {modeLabel}{exLabel}{idLabel} | table={AppConfig.Current.DbTable} → {total:N0}건");

            // 전체를 한 번에 로드(백그라운드) + 스니펫까지 미리 계산해 FullRow 로.
            // body 원본(5000자)은 버리고 스니펫만 메모리에 — 대량도 메모리 가벼움.
            var built = await Task.Run(() =>
            {
                var raw = _search.Search(kw, target, mode,
                    limit: total, offset: 0, includeExcluded: includeExcluded, idMin: idMin, idMax: idMax);
                var kws = SearchService.PrepareKeywords(kw, mode);
                var list = new List<FullRow>(raw.Count);
                foreach (var r in raw)
                {
                    var isExcluded = string.IsNullOrEmpty(r.BodyChunk);
                    var preview = isExcluded ? "" : SearchService.ExtractSnippet(r.BodyChunk, kws);
                    list.Add(new FullRow(
                        r.Id, r.Directory, r.Filename, preview,
                        r.ParsedAt?.ToString("yyyy-MM-dd HH:mm") ?? "", isExcluded));
                }
                return list;
            });

            _total = total;
            _rows.Clear();
            _rows.AddRange(built);
            _list.VirtualListSize = _rows.Count;
            _list.Invalidate();
            UpdateStatus();
            OnSelectionChanged();
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

    // VirtualMode — 보이는 행만 on-demand 로 ListViewItem 생성.
    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count)
        {
            e.Item = new ListViewItem("");
            return;
        }
        var r = _rows[e.ItemIndex];
        var fnShort = r.Filename.Length > 53 ? r.Filename[..50] + "…" : r.Filename;
        var previewShort = r.PreviewFull.Length > 150 ? r.PreviewFull[..150] + "…" : r.PreviewFull;
        // subitem 순서 = 컬럼 순서 (ID, 폴더, 파일명, 적재일, 미리보기).
        var item = new ListViewItem(new[] { r.Id.ToString(), r.Directory, fnShort, r.ParsedAtStr, previewShort })
        {
            UseItemStyleForSubItems = true,
        };
        // 제외 행은 italic (ForeColor 는 selection highlight 와 충돌해 안 건드림).
        if (r.IsExcluded) item.Font = _italicFont;
        e.Item = item;
    }

    private void UpdateStatus()
        => _statusLabel.Text = $"{_total:N0}건";

    private void OnToggleIdFilter()
    {
        _idMinBox.Enabled = _idFilterBox.Checked;
        _idMaxBox.Enabled = _idFilterBox.Checked;
    }

    // 적재일 컬럼은 항상 데이터가 채워져 있고 width 만 토글 — 재검색 불필요.
    private void ToggleParsedAtColumn()
        => _parsedAtCol.Width = _showParsedAtBox.Checked ? 140 : 0;

    // 선택된 행 — VirtualMode 라 SelectedIndices → _rows 로 접근.
    private List<FullRow> SelectedRows()
        => _list.SelectedIndices.Cast<int>()
            .Where(i => i >= 0 && i < _rows.Count)
            .Select(i => _rows[i]).ToList();

    private void OnSelectionChanged()
    {
        var sel = SelectedRows();
        if (sel.Count == 0)
        {
            _openBtn.Enabled = false;
            _openPathBtn.Enabled = false;
            _delBtn.Enabled = false;
            _delBtn.Text = "제외 (Del)";
            _infoLabel.Text = InfoDefault;
            _infoLabel.ForeColor = Color.Gray;
            return;
        }

        var anyExcluded = sel.Any(r => r.IsExcluded);
        var anyNormal   = sel.Any(r => !r.IsExcluded);
        if (anyExcluded && anyNormal)
        {
            _delBtn.Enabled = false;
            _delBtn.Text = "혼합 선택 불가";
        }
        else if (anyExcluded)
        {
            _delBtn.Enabled = true;
            _delBtn.Text = "완전 삭제 (Del)";
        }
        else
        {
            _delBtn.Enabled = true;
            _delBtn.Text = "제외 (Del)";
        }

        _openBtn.Enabled     = sel.Count == 1;
        _openPathBtn.Enabled = sel.Count == 1;

        if (sel.Count == 1)
        {
            var fp = Path.Combine(sel[0].Directory, sel[0].Filename);
            _infoLabel.Text = fp.Length <= 110 ? fp : fp[..107] + "…";
            _infoLabel.ForeColor = Color.Black;
        }
        else
        {
            _infoLabel.Text = $"{sel.Count}건 선택됨";
            _infoLabel.ForeColor = Color.Black;
        }
    }

    // ─ 파일 열기 / 경로 열기 / 제외 / 삭제 ───────────────────────────

    private void OpenSelectedFile()
    {
        var sel = SelectedRows();
        if (sel.Count != 1) return;
        var row = sel[0];
        var fp = Path.Combine(row.Directory, row.Filename);
        if (!File.Exists(fp))
        {
            MessageBox.Show(this, $"파일을 찾을 수 없습니다:\n{fp}", "파일 없음");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(fp) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "열기 실패"); }
    }

    private void OpenSelectedPath()
    {
        var sel = SelectedRows();
        if (sel.Count != 1) return;
        var row = sel[0];
        var fp = Path.Combine(row.Directory, row.Filename);
        try
        {
            if (File.Exists(fp))
            {
                var norm = Path.GetFullPath(fp);
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{norm}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(row.Directory))
            {
                Process.Start(new ProcessStartInfo(Path.GetFullPath(row.Directory)) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show(this, $"경로를 찾을 수 없습니다:\n{row.Directory}", "경로 없음");
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "열기 실패"); }
    }

    private void DeleteSelected()
    {
        // 내림차순 인덱스 — 삭제 시 _rows.RemoveAt 가 앞 인덱스를 흔들지 않게.
        var idxs = _list.SelectedIndices.Cast<int>()
            .Where(i => i >= 0 && i < _rows.Count)
            .OrderByDescending(i => i).ToList();
        if (idxs.Count == 0) return;

        var rows = idxs.Select(i => _rows[i]).ToList();
        var allExcluded = rows.All(r => r.IsExcluded);
        var allNormal   = rows.All(r => !r.IsExcluded);
        if (!allExcluded && !allNormal) return;
        var ids = rows.Select(r => r.Id).ToList();

        try
        {
            if (allExcluded)
            {
                if (MessageBox.Show(this,
                    $"{ids.Count}건을 DB 에서 완전히 삭제합니다. 되돌릴 수 없습니다.",
                    "완전 삭제", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                var n = _repo.DeleteRows(ids);
                _log.AppendLine($"[삭제] {n}건 완전 삭제");
                foreach (var i in idxs) _rows.RemoveAt(i);   // 내림차순이라 안전
                _total -= n;
            }
            else
            {
                var n = _repo.NullifyBodyText(ids);
                _log.AppendLine($"[제외] {n}건 body_text NULL 처리");
                foreach (var i in idxs)
                    _rows[i] = _rows[i] with { IsExcluded = true, PreviewFull = "" };
            }
            _list.SelectedIndices.Clear();
            _list.VirtualListSize = _rows.Count;
            _list.Invalidate();
            UpdateStatus();
            OnSelectionChanged();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "DB 오류"); }
    }

    // ─ Ctrl+C 클립보드 ───────────────────────────────────────────────

    private void CopySelectedFiles()
    {
        var sel = SelectedRows();
        if (sel.Count == 0) return;
        var paths = sel
            .Select(r => Path.Combine(r.Directory, r.Filename))
            .Where(File.Exists)
            .ToList();
        if (paths.Count == 0)
        {
            _log.AppendLine("[클립보드] 복사할 실제 파일이 없습니다.");
            return;
        }
        if (FileClipboard.SetFileDropList(paths))
            _log.AppendLine($"[클립보드] {paths.Count}건 복사 — Explorer 에서 Ctrl+V");
        else
            _log.AppendLine("[클립보드] 복사 실패 (DRM 차단 가능성)");
    }

    // ─ 드래그 ────────────────────────────────────────────────────────

    private void OnListItemDrag(object? sender, ItemDragEventArgs e)
    {
        var sel = SelectedRows();
        if (sel.Count == 0) return;
        var paths = sel
            .Select(r => Path.Combine(r.Directory, r.Filename))
            .Where(File.Exists)
            .ToArray();
        if (paths.Length == 0) return;
        try
        {
            // System.Windows.Forms.DataObject 의 FileDrop 포맷 — Explorer 가 받아 복사로 처리.
            // DRM 후킹이 OLE Drag 단계를 막을 수 있는데, 그 경우 사용자는 Ctrl+C 우회 사용.
            _list.DoDragDrop(new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            _log.AppendLine($"[드래그 실패] {ex.Message}");
        }
    }

    // ─ 우클릭 컨텍스트 메뉴 ──────────────────────────────────────────

    private void OnListMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = _list.HitTest(e.Location);
        if (hit.Item is null) return;
        // VirtualMode — 우클릭한 행이 선택 안 돼 있으면 단독 선택으로 전환.
        var idx = hit.Item.Index;
        if (!_list.SelectedIndices.Contains(idx)) { _list.SelectedIndices.Clear(); _list.SelectedIndices.Add(idx); }

        var subIdx = hit.Item.SubItems.IndexOf(hit.SubItem ?? hit.Item.SubItems[0]);
        var menu = new ContextMenuStrip();

        switch (subIdx)
        {
            case ColDir:     menu.Items.Add("폴더 경로 복사",   null, (_, _) => CopyField(ColDir));     menu.Items.Add(new ToolStripSeparator()); break;
            case ColFile:    menu.Items.Add("파일명 복사",       null, (_, _) => CopyField(ColFile));    menu.Items.Add(new ToolStripSeparator()); break;
            case ColPreview: menu.Items.Add("내용 미리보기 복사", null, (_, _) => CopyField(ColPreview)); menu.Items.Add(new ToolStripSeparator()); break;
        }

        var singleSel = _list.SelectedIndices.Count == 1;
        menu.Items.Add("파일 열기", null, (_, _) => OpenSelectedFile()).Enabled = singleSel;
        menu.Items.Add("경로 열기", null, (_, _) => OpenSelectedPath()).Enabled = singleSel;
        menu.Items.Add(new ToolStripSeparator());

        if (_delBtn.Enabled && _delBtn.Text != "혼합 선택 불가")
            menu.Items.Add(_delBtn.Text, null, (_, _) => DeleteSelected());
        else
            menu.Items.Add("혼합 선택 불가").Enabled = false;

        menu.Show(_list, e.Location);
    }

    private void CopyField(int fieldIdx)
    {
        var sel = SelectedRows();
        if (sel.Count == 0) return;
        var vals = new List<string>();
        foreach (var r in sel)
        {
            vals.Add(fieldIdx switch
            {
                ColDir     => r.Directory,
                ColFile    => r.Filename,
                ColPreview => r.PreviewFull,
                _          => r.Id.ToString(),
            });
        }
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, vals));
            _log.AppendLine($"[복사] {vals.Count}건");
        }
        catch (Exception ex) { _log.AppendLine($"[복사 실패] {ex.Message}"); }
    }

    // ─ hover tooltip — Timer 기반 ────────────────────────────────────

    private void OnListMouseMove(object? sender, MouseEventArgs e)
    {
        // 마우스가 움직이면 무조건 timer reset + 기존 tooltip hide.
        // 같은 자리에서 500ms 정지하면 Tick 에서 다시 표시.
        if (e.Location == _lastHoverPos) return;
        _lastHoverPos = e.Location;
        HideTooltip();
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void OnHoverTimerTick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        // 마지막 마우스 위치에서 hit-test. 마우스가 ListView 영역 밖이면 무시.
        var clientPos = _list.PointToClient(Cursor.Position);
        if (!_list.ClientRectangle.Contains(clientPos)) return;

        var hit = _list.HitTest(clientPos);
        if (hit.Item is null) return;
        var rowIdx = hit.Item.Index;
        if (rowIdx < 0 || rowIdx >= _rows.Count) return;
        var subIdx = hit.Item.SubItems.IndexOf(hit.SubItem ?? hit.Item.SubItems[0]);

        var row = _rows[rowIdx];
        var text = subIdx switch
        {
            ColDir     => row.Directory,
            ColFile    => row.Filename,
            ColPreview => row.PreviewFull,
            _          => "",
        };
        if (string.IsNullOrEmpty(text) || text.Length < 30) return;
        if (text.Length > 800) text = text[..800] + " …";

        var kws = subIdx == ColPreview
            ? SearchService.PrepareKeywords(_lastKw, _lastMode)
            : Array.Empty<string>();

        var screen = _list.PointToScreen(clientPos);
        ShowTooltip(screen, text, kws);
    }

    private void ShowTooltip(Point screenPos, string text, IReadOnlyList<string> keywords)
    {
        HideTooltip();
        _tooltip = new ToolTipForm(text, keywords);
        var x = screenPos.X + 15;
        var y = screenPos.Y + 10;
        var bounds = Screen.FromPoint(screenPos).WorkingArea;
        if (x + _tooltip.Width  > bounds.Right)  x = bounds.Right - _tooltip.Width - 10;
        if (y + _tooltip.Height > bounds.Bottom) y = screenPos.Y - _tooltip.Height - 10;
        _tooltip.Location = new Point(x, y);
        // owner 없이 top-level Form 으로 표시 — ListView 의 z-order/paint 와 충돌 차단.
        _tooltip.Show();
    }

    private void HideTooltip()
    {
        if (_tooltip is null) return;
        _tooltip.Close();
        _tooltip.Dispose();
        _tooltip = null;
    }
}

/// <summary>
/// 커스텀 tooltip — top-level Form, 비활성 표시. 키워드 매치를 빨강 굵게 강조.
/// owner 가 없어 ListView 의 z-order 와 무관하게 떠 있음.
///
/// 디자인 메모 (Win11 시스템 ToolTip 톤에 근접):
///   - 외곽 1px 부드러운 회색 (검정 FixedSingle 은 너무 강함)
///   - 내부 8px 패딩 (텍스트가 가장자리에 붙는 옛 룩 회피)
///   - 배경 SystemColors.Info (#FFFFE1) — OS 표준 ToolTip 색
///   - Width 텍스트 측정 후 220~560 사이 동적
///   - Height 실측 + 안전 마진
/// </summary>
internal sealed class ToolTipForm : Form
{
    private const int OuterBorder = 1;   // 외곽 회색 테두리 두께
    private const int InnerPadding = 8;  // 내부 여백
    private const int MinWidth = 220;
    private const int MaxWidth = 560;

    public ToolTipForm(string text, IReadOnlyList<string> keywords)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoSize = false;
        // Form 의 BackColor 가 외곽 1px 영역으로 노출됨 — 부드러운 회색 테두리 역할.
        BackColor = Color.FromArgb(0x9A, 0x9A, 0x9A);
        Padding = new Padding(OuterBorder);

        var font = new Font("맑은 고딕", 9);

        // 내부 패딩 패널 — RichTextBox 가 가장자리에 텍스트 붙는 룩 회피.
        var pad = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Info,
            Padding = new Padding(InnerPadding),
        };

        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Info,
            ForeColor = Color.FromArgb(0x22, 0x22, 0x22),
            Font = font,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.None,
            DetectUrls = false,
            Cursor = Cursors.Arrow,
            Multiline = true,
        };
        rtb.Text = text;

        if (keywords.Count > 0)
        {
            var lower = text.ToLowerInvariant();
            var boldRed = new Font(rtb.Font, FontStyle.Bold);
            foreach (var kw in keywords)
            {
                if (string.IsNullOrEmpty(kw)) continue;
                var kwLower = kw.ToLowerInvariant();
                var pos = 0;
                while (true)
                {
                    var found = lower.IndexOf(kwLower, pos, StringComparison.Ordinal);
                    if (found < 0) break;
                    rtb.Select(found, kw.Length);
                    rtb.SelectionColor = Color.Red;
                    rtb.SelectionFont  = boldRed;
                    pos = found + kw.Length;
                }
            }
            rtb.Select(0, 0);
        }

        pad.Controls.Add(rtb);
        Controls.Add(pad);

        // Width — 한 줄에 충분하지만 너무 넓지 않게.
        // 짧은 텍스트(파일명 hover) 는 한 줄, 긴 텍스트(미리보기) 는 MaxWidth 에서 WordBreak.
        var measured = TextRenderer.MeasureText(
            text, font,
            new Size(MaxWidth - InnerPadding * 2, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        var w = Math.Clamp(measured.Width + InnerPadding * 2, MinWidth, MaxWidth);
        var h = measured.Height + InnerPadding * 2 + 4;  // +4 안전 마진
        ClientSize = new Size(w + OuterBorder * 2, h + OuterBorder * 2);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }
}
