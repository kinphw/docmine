// EnvCompareList — 두 환경(현재 ↔ 대조본)의 합집합 대조 리스트 (반출·반입 공유).
//
// 가상화 ListView + 의사 체크박스 + 두 presence 컬럼 + 표시 필터.
//   ㆍ base 행(액션 대상)만 체크 가능. 대조에만 있는 "유령 행"은 회색 표시 전용.
//   ㆍ 두 컬럼(baseLabel / compareLabel)에 ✓ 로 각 집합 포함여부 표시.
//   ㆍ 필터: 전체 / {base}만 / 양쪽 / {compare}만.
//   ㆍ 행 클릭=선택 토글, Shift+클릭=기준점 동작을 범위에 적용(체크/해제 모두).
//
// WinForms VirtualMode 는 CheckBoxes 미지원 → '선택' 컬럼에 ☑/☐ 를 직접 그린다.

namespace DocMine.UI.Tabs;

/// <summary>합집합 한 행. base(액션 대상) 또는 대조 전용(유령).</summary>
public sealed class CompareRow
{
    public required (string Dir, string Fn) Key { get; init; }
    public bool InBase { get; init; }
    public bool InCompare { get; init; }
    public required string[] Cells { get; init; }   // Configure 의 dataColumns 순서
    public object? Item { get; init; }              // 액션 backref (ExportRow/DocRecord). 유령=null
}

public sealed class EnvCompareList : UserControl
{
    public readonly record struct ColumnDef(string Header, int Width, HorizontalAlignment Align);

    private readonly ListView _list;
    private readonly RadioButton _fAll, _fOnlyBase, _fBoth, _fOnlyCompare;
    private readonly Button _selAll, _selNone;
    private readonly Label _selInfo, _hint;

    private string _baseLabel = "현재환경", _compareLabel = "대조";
    private ColumnHeader _compareCol = null!;
    private int _dataColCount;
    private bool _compareLoaded;

    private readonly List<CompareRow> _all = new();
    private readonly List<CompareRow> _view = new();
    private readonly HashSet<(string, string)> _checked = new();
    private int _anchor = -1;
    private bool _anchorChecked;

    public event Action? SelectionChanged;

    public EnvCompareList()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 4) };
        bar.Controls.Add(new Label { Text = "표시:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _fAll        = new RadioButton { Text = "전체", AutoSize = true, Checked = true, Enabled = false, Margin = new Padding(0, 4, 0, 0) };
        _fOnlyBase   = new RadioButton { AutoSize = true, Enabled = false, Margin = new Padding(8, 4, 0, 0) };  // {compare}에 없는 것만
        _fBoth       = new RadioButton { Text = "양쪽 모두", AutoSize = true, Enabled = false, Margin = new Padding(8, 4, 0, 0) };
        _fOnlyCompare = new RadioButton { AutoSize = true, Enabled = false, Margin = new Padding(8, 4, 0, 0) }; // {base}에 없는 것만
        foreach (var rb in new[] { _fAll, _fOnlyBase, _fBoth, _fOnlyCompare })
        {
            rb.CheckedChanged += (s, _) => { if (((RadioButton)s!).Checked) ApplyFilter(); };
            bar.Controls.Add(rb);
        }
        _selAll  = new Button { Text = "전체 선택", AutoSize = true, Enabled = false, Margin = new Padding(16, 0, 0, 0) };
        _selNone = new Button { Text = "전체 해제", AutoSize = true, Enabled = false, Margin = new Padding(4, 0, 0, 0) };
        _selAll.Click  += (_, _) => SetAllChecked(true);
        _selNone.Click += (_, _) => SetAllChecked(false);
        bar.Controls.Add(_selAll);
        bar.Controls.Add(_selNone);
        _selInfo = new Label { Text = "0건 선택됨", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(12, 6, 0, 0) };
        bar.Controls.Add(_selInfo);
        _hint = new Label { Text = "(행 클릭=선택 · Shift+클릭=범위)", AutoSize = true, ForeColor = Color.DarkGray, Padding = new Padding(12, 6, 0, 0) };
        bar.Controls.Add(_hint);

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            HideSelection = false, MultiSelect = false, GridLines = false,
            Font = new Font("맑은 고딕", 9), VirtualMode = true,
        };
        _list.RetrieveVirtualItem += OnRetrieveItem;
        _list.MouseClick += OnMouseClick;

        Controls.Add(_list);   // Fill 먼저, Top 바 나중 (z-order)
        Controls.Add(bar);
    }

    /// <summary>컬럼 구성 — 한 번만 호출. dataColumns 는 선택/base/compare 뒤에 붙는 데이터 컬럼.</summary>
    public void Configure(string baseLabel, string compareLabel, params ColumnDef[] dataColumns)
    {
        _baseLabel = baseLabel;
        _compareLabel = compareLabel;
        _dataColCount = dataColumns.Length;

        _list.Columns.Clear();
        _list.Columns.Add("선택", 44, HorizontalAlignment.Center);
        _list.Columns.Add(baseLabel, 64, HorizontalAlignment.Center);
        _compareCol = _list.Columns.Add(compareLabel, 0, HorizontalAlignment.Center);   // 로드 전 숨김
        foreach (var c in dataColumns)
            _list.Columns.Add(c.Header, c.Width, c.Align);

        // "없는 것" 관점 라벨 — 매니페스트(=compare)에 없는 것 / 현재환경(=base)에 없는 것.
        _fOnlyBase.Text    = $"{compareLabel}에 없는 것만";   // InBase && !InCompare
        _fOnlyCompare.Text = $"{baseLabel}에 없는 것만";      // !InBase && InCompare (유령)
    }

    /// <summary>행 공급 + 대조 로드 여부. 호출 시 선택/필터 초기화.</summary>
    public void SetRows(IReadOnlyList<CompareRow> rows, bool compareLoaded)
    {
        _all.Clear();
        _all.AddRange(rows);
        _compareLoaded = compareLoaded;
        _compareCol.Width = compareLoaded ? 64 : 0;
        foreach (var rb in new[] { _fAll, _fOnlyBase, _fBoth, _fOnlyCompare }) rb.Enabled = compareLoaded;
        if (!compareLoaded) _fAll.Checked = true;   // 대조 미로드 → 항상 전체
        _checked.Clear();
        _anchor = -1;
        ApplyFilter();
    }

    public IReadOnlyList<object> SelectedItems =>
        _all.Where(r => r.InBase && r.Item is not null && _checked.Contains(r.Key))
            .Select(r => r.Item!).ToList();

    public int SelectedCount => _checked.Count;

    private void ApplyFilter()
    {
        _view.Clear();
        // 0 전체 / 1 compare에 없는것(InBase&&!InCompare) / 2 양쪽 / 3 base에 없는것(유령)
        int f = _fAll.Checked ? 0 : _fOnlyBase.Checked ? 1 : _fBoth.Checked ? 2 : 3;
        foreach (var r in _all)
        {
            bool show = !_compareLoaded || f == 0 || f switch
            {
                1 => r.InBase && !r.InCompare,
                2 => r.InBase && r.InCompare,
                3 => !r.InBase && r.InCompare,
                _ => true,
            };
            if (show) _view.Add(r);
        }
        _anchor = -1;
        _list.VirtualListSize = _view.Count;
        _list.Invalidate();

        var anyBase = _all.Any(r => r.InBase);
        _selAll.Enabled = anyBase;
        _selNone.Enabled = anyBase;
        UpdateInfo();
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _view.Count) { e.Item = new ListViewItem(""); return; }
        var r = _view[e.ItemIndex];

        var cells = new string[3 + _dataColCount];
        cells[0] = r.InBase ? (_checked.Contains(r.Key) ? "☑" : "☐") : "";   // 유령은 체크 불가
        cells[1] = r.InBase ? "✓" : "";
        cells[2] = _compareLoaded ? (r.InCompare ? "✓" : "") : "";
        for (int i = 0; i < _dataColCount; i++)
            cells[3 + i] = i < r.Cells.Length ? r.Cells[i] : "";

        var item = new ListViewItem(cells);
        if (!r.InBase) item.ForeColor = Color.Gray;   // 대조 전용(유령) 행 흐리게
        e.Item = item;
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        var hit = _list.HitTest(e.Location);
        if (hit.Item is null) return;
        var idx = hit.Item.Index;
        if (idx < 0 || idx >= _view.Count) return;

        if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift && _anchor >= 0 && _anchor < _view.Count)
        {
            // 기준점에서 한 동작(체크/해제)을 범위 전체에 적용. 유령 행은 건너뜀.
            var lo = Math.Min(_anchor, idx);
            var hi = Math.Max(_anchor, idx);
            for (var i = lo; i <= hi; i++)
            {
                var rr = _view[i];
                if (!rr.InBase) continue;
                if (_anchorChecked) _checked.Add(rr.Key); else _checked.Remove(rr.Key);
            }
            _list.Invalidate();
        }
        else
        {
            var r = _view[idx];
            if (!r.InBase) return;   // 유령 행은 선택 불가
            _anchorChecked = _checked.Add(r.Key);
            if (!_anchorChecked) _checked.Remove(r.Key);
            _anchor = idx;
            _list.Invalidate(hit.Item.Bounds);
        }
        UpdateInfo();
    }

    private void SetAllChecked(bool value)
    {
        _checked.Clear();
        if (value)
            foreach (var r in _view)
                if (r.InBase) _checked.Add(r.Key);
        _list.Invalidate();
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        _selInfo.Text = $"{_checked.Count:N0}건 선택됨 (표시 {_view.Count:N0})";
        SelectionChanged?.Invoke();
    }
}
