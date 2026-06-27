// EnvCompareList — 현재 목록(base) 을 기준으로, 대조본 보유 여부를 한 컬럼으로 표시 (반출·반입 공유).
//
// base 가 *항상 기준*이다 — base 의 전체를 보여준다(유령 행 없음). 대조본 보유 여부만 ✓ 로 표기.
//
// 선택 버튼 3개:  [전체 선택]  [전체 해제]  [{compare}에 없는 것만 선택]
//   마지막 = "아직 안 넘어간 신규만" 한 번에 선택.
// 표시 필터:  [☑ {compare}에 없는 것만 표시]  — 화면을 신규만으로 좁혀 본다(선택과 별개).
// 행 클릭=토글, Shift+클릭=범위.
//
// WinForms VirtualMode 는 CheckBoxes 미지원 → '선택' 컬럼에 ☑/☐ 를 직접 그린다.
//
// 헤더 클릭 정렬: 데이터 컬럼 헤더를 누르면 오름/내림 토글. VirtualMode 라
// ListViewItemSorter 를 못 쓰므로 backing 리스트(_rows)를 직접 정렬해 반영한다.

using System.Globalization;

namespace DocMine.UI.Tabs;

/// <summary>리스트 한 행(전부 base = 선택 가능). InCompare = 대조본 보유 여부.</summary>
public sealed class CompareRow
{
    public required (string Dir, string Fn) Key { get; init; }
    public bool InCompare { get; init; }
    public required string[] Cells { get; init; }   // Configure 의 dataColumns 순서
    public object? Item { get; init; }              // 액션 backref (ExportRow/DocRecord)
}

public sealed class EnvCompareList : UserControl
{
    /// <summary>셀 정렬 방식 — Auto(숫자로 파싱되면 숫자, 아니면 문화권 문자열) /
    /// Size("1.2 MB"·"340 KB" 단위 역파싱하여 바이트 크기로 비교).</summary>
    public enum CellSort { Auto, Size }

    public readonly record struct ColumnDef(string Header, int Width, HorizontalAlignment Align, CellSort Sort = CellSort.Auto);

    private readonly ListView _list;
    private readonly Button _selAll, _selNone, _selNotInCompare;
    private readonly CheckBox _showOnlyNew;
    private readonly Label _selInfo, _hint;

    private ColumnHeader _compareCol = null!;
    private int _dataColCount;
    private bool _compareLoaded;

    // 헤더 클릭 정렬 상태 (선택/대조 컬럼 제외, 데이터 컬럼만).
    private string[] _baseHeaders = Array.Empty<string>();   // 화살표 제거용 원본 헤더(전체 컬럼)
    private CellSort[] _sortDefs = Array.Empty<CellSort>();   // dataColumns 의 정렬 방식
    private int _sortCol = -1;     // 정렬 중인 전체-컬럼 인덱스 (-1 = 정렬 없음)
    private bool _sortAsc = true;

    private readonly List<CompareRow> _rows = new();   // 전체(base)
    private readonly List<CompareRow> _view = new();   // 표시 필터 적용분
    private readonly HashSet<(string, string)> _checked = new();
    private int _anchor = -1;
    private bool _anchorChecked;

    public event Action? SelectionChanged;

    public EnvCompareList()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 4) };
        bar.Controls.Add(new Label { Text = "선택:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _selAll          = new Button { Text = "전체 선택", AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 4, 0) };
        _selNone         = new Button { Text = "전체 해제", AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 4, 0) };
        _selNotInCompare = new Button { AutoSize = true, Enabled = false, Visible = false, Margin = new Padding(0, 0, 0, 0) };  // {compare}에 없는 것만 선택
        _selAll.Click          += (_, _) => SelectGroup(_ => true);
        _selNone.Click         += (_, _) => SelectGroup(_ => false);
        _selNotInCompare.Click += (_, _) => SelectGroup(r => !r.InCompare);
        bar.Controls.Add(_selAll);
        bar.Controls.Add(_selNone);
        bar.Controls.Add(_selNotInCompare);
        _showOnlyNew = new CheckBox { AutoSize = true, Visible = false, Margin = new Padding(16, 4, 0, 0) };   // {compare}에 없는 것만 표시
        _showOnlyNew.CheckedChanged += (_, _) => ApplyFilter();
        bar.Controls.Add(_showOnlyNew);
        _selInfo = new Label { Text = "0건 선택됨", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(16, 6, 0, 0) };
        bar.Controls.Add(_selInfo);
        _hint = new Label { Text = "(행 클릭=토글 · Shift+클릭=범위)", AutoSize = true, ForeColor = Color.DarkGray, Padding = new Padding(12, 6, 0, 0) };
        bar.Controls.Add(_hint);

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            HideSelection = false, MultiSelect = false, GridLines = false,
            Font = new Font("맑은 고딕", 9), VirtualMode = true,
        };
        _list.RetrieveVirtualItem += OnRetrieveItem;
        _list.MouseClick += OnMouseClick;
        _list.ColumnClick += OnColumnClick;

        Controls.Add(_list);   // Fill 먼저, Top 바 나중 (z-order)
        Controls.Add(bar);
    }

    /// <summary>컬럼 구성 — 재호출 가능(모드 전환 시 컬럼 교체). 선택 + 대조(✓) 뒤에 dataColumns.</summary>
    public void Configure(string compareLabel, params ColumnDef[] dataColumns)
    {
        _dataColCount = dataColumns.Length;
        _sortDefs = dataColumns.Select(c => c.Sort).ToArray();
        _sortCol = -1;   // 컬럼 구성이 바뀌면 직전 정렬 인덱스는 무효 — 초기화.
        _sortAsc = true;

        _list.Columns.Clear();
        _list.Columns.Add("선택", 44, HorizontalAlignment.Center);
        _compareCol = _list.Columns.Add(compareLabel, 0, HorizontalAlignment.Center);   // 대조 로드 전 숨김
        foreach (var c in dataColumns)
            _list.Columns.Add(c.Header, c.Width, c.Align);

        // 정렬 화살표를 붙였다 떼기 위한 원본 헤더 텍스트 보존.
        _baseHeaders = _list.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToArray();

        _selNotInCompare.Text = $"{compareLabel}에 없는 것만 선택";
        _showOnlyNew.Text     = $"{compareLabel}에 없는 것만 표시";
    }

    /// <summary>행 공급 + 대조 로드 여부. 호출 시 선택 초기화(표시 필터 상태는 유지).</summary>
    public void SetRows(IReadOnlyList<CompareRow> rows, bool compareLoaded)
    {
        _rows.Clear();
        _rows.AddRange(rows);
        _compareLoaded = compareLoaded;
        _compareCol.Width = compareLoaded ? 64 : 0;
        _selNotInCompare.Visible = compareLoaded;
        _showOnlyNew.Visible = compareLoaded;

        _checked.Clear();
        _anchor = -1;
        if (_sortCol >= 2) SortRows();   // 재검색/재대조 후에도 직전 정렬 기준 유지
        ApplyFilter();
    }

    public IReadOnlyList<object> SelectedItems =>
        _rows.Where(r => r.Item is not null && _checked.Contains(r.Key)).Select(r => r.Item!).ToList();

    public int SelectedCount => _checked.Count;

    private void ApplyFilter()
    {
        bool hide = _compareLoaded && _showOnlyNew.Checked;
        _view.Clear();
        foreach (var r in _rows)
            if (!hide || !r.InCompare) _view.Add(r);

        // 화면에서 사라진 행의 선택은 정리 — "보이는 것만 선택됨" 일관.
        if (hide)
        {
            var visible = _view.Select(r => r.Key).ToHashSet();
            _checked.IntersectWith(visible);
        }

        _anchor = -1;
        _list.VirtualListSize = _view.Count;
        _list.Invalidate();

        var any = _view.Count > 0;
        _selAll.Enabled = any;
        _selNone.Enabled = any;
        _selNotInCompare.Enabled = _compareLoaded && any;
        UpdateInfo();
    }

    // ─ 헤더 클릭 정렬 ────────────────────────────────────────────────
    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        // 선택(0)·대조(1) 컬럼은 정렬 제외 — 데이터 컬럼만. 셀이 OnRetrieveItem 에서
        // 동적 생성(_checked/InCompare)이라 _rows.Cells 로 정렬 불가하기도 하다.
        if (e.Column < 2 || _rows.Count == 0) return;

        if (_sortCol == e.Column) _sortAsc = !_sortAsc;
        else { _sortCol = e.Column; _sortAsc = true; }

        SortRows();
        ApplyFilter();   // _view 재구성 + 다시 그림 (선택은 Key 기반이라 정렬과 무관하게 유지)
    }

    // _rows 를 현재 정렬 컬럼/방향으로 in-place 정렬하고 헤더 화살표를 갱신.
    private void SortRows()
    {
        if (_sortCol < 2) return;
        int dataIdx = _sortCol - 2;   // Cells 인덱스 (선택·대조 2개 앞섬)
        var sort = dataIdx >= 0 && dataIdx < _sortDefs.Length ? _sortDefs[dataIdx] : CellSort.Auto;
        int dir = _sortAsc ? 1 : -1;
        _rows.Sort((x, y) =>
        {
            var a = dataIdx < x.Cells.Length ? x.Cells[dataIdx] : "";
            var b = dataIdx < y.Cells.Length ? y.Cells[dataIdx] : "";
            return dir * CompareCells(a, b, sort);
        });
        UpdateSortIndicator();
    }

    private void UpdateSortIndicator()
    {
        if (_baseHeaders.Length != _list.Columns.Count) return;
        for (int i = 0; i < _list.Columns.Count; i++)
        {
            var arrow = i == _sortCol ? (_sortAsc ? "  ▲" : "  ▼") : "";
            _list.Columns[i].Text = _baseHeaders[i] + arrow;
        }
    }

    private static int CompareCells(string a, string b, CellSort sort)
    {
        if (sort == CellSort.Size) return ParseSizeCell(a).CompareTo(ParseSizeCell(b));
        // Auto — 양쪽 다 숫자로 파싱되면 숫자, 아니면 문화권 문자열(한국어 정렬).
        if (TryNum(a, out var da) && TryNum(b, out var db)) return da.CompareTo(db);
        return string.Compare(a, b, StringComparison.CurrentCulture);
    }

    private static bool TryNum(string s, out double v)
        => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v);

    // "1.2 MB" / "340 KB" → 비교용 바이트 근사. (표시 정밀도 기준 — 정렬 목적엔 충분.)
    private static double ParseSizeCell(string s)
    {
        s = s.Trim();
        double mult = 1;
        if (s.EndsWith("GB", StringComparison.OrdinalIgnoreCase)) { mult = 1024d * 1024 * 1024; s = s[..^2]; }
        else if (s.EndsWith("MB", StringComparison.OrdinalIgnoreCase)) { mult = 1024d * 1024; s = s[..^2]; }
        else if (s.EndsWith("KB", StringComparison.OrdinalIgnoreCase)) { mult = 1024d; s = s[..^2]; }
        else if (s.EndsWith("B", StringComparison.OrdinalIgnoreCase)) { s = s[..^1]; }
        return TryNum(s.Trim(), out var v) ? v * mult : 0;
    }

    // 조건에 맞는 *보이는* 행으로 선택을 '교체'(누적 아님).
    private void SelectGroup(Func<CompareRow, bool> predicate)
    {
        _checked.Clear();
        foreach (var r in _view)
            if (predicate(r)) _checked.Add(r.Key);
        _list.Invalidate();
        UpdateInfo();
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _view.Count) { e.Item = new ListViewItem(""); return; }
        var r = _view[e.ItemIndex];

        var cells = new string[2 + _dataColCount];
        cells[0] = _checked.Contains(r.Key) ? "☑" : "☐";
        cells[1] = _compareLoaded ? (r.InCompare ? "✓" : "") : "";
        for (int i = 0; i < _dataColCount; i++)
            cells[2 + i] = i < r.Cells.Length ? r.Cells[i] : "";

        e.Item = new ListViewItem(cells);
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        var hit = _list.HitTest(e.Location);
        if (hit.Item is null) return;
        var idx = hit.Item.Index;
        if (idx < 0 || idx >= _view.Count) return;

        if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift && _anchor >= 0 && _anchor < _view.Count)
        {
            var lo = Math.Min(_anchor, idx);
            var hi = Math.Max(_anchor, idx);
            for (var i = lo; i <= hi; i++)
            {
                var key = _view[i].Key;
                if (_anchorChecked) _checked.Add(key); else _checked.Remove(key);
            }
            _list.Invalidate();
        }
        else
        {
            var key = _view[idx].Key;
            _anchorChecked = _checked.Add(key);
            if (!_anchorChecked) _checked.Remove(key);
            _anchor = idx;
            _list.Invalidate(hit.Item.Bounds);
        }
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        _selInfo.Text = $"{_checked.Count:N0}건 선택됨 (표시 {_view.Count:N0})";
        SelectionChanged?.Invoke();
    }
}
