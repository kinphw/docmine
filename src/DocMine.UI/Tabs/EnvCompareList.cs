// EnvCompareList — 두 환경(현재 ↔ 대조본)의 합집합 대조 리스트 (반출·반입 공유).
//
// 가상화 ListView + 의사 체크박스 + 두 presence 컬럼.
//   ㆍ 두 컬럼(baseLabel / compareLabel)에 ✓ 로 각 집합 포함여부 표시.
//   ㆍ [선택 대상] 라디오로 "무엇을 선택할지" 지정 — 대상 아닌 행은 회색 + 선택 불가(잠금),
//      맥락은 그대로 보인다. (예: 신규만 선택하되 기적재는 회색으로 보임)
//        · 전체            : 모든 base 행 선택 가능
//        · {compare}에 없는 것 : 차집합(base − compare) 만 선택
//        · 양쪽에 있는 것     : 교집합 만 선택
//   ㆍ [선택 대상만 보기] 체크 시 비대상 행을 아예 숨김(예전 숨김 필터와 동일).
//   ㆍ 행 클릭=선택 토글, Shift+클릭=기준점 동작을 범위에 적용.
//
// WinForms VirtualMode 는 CheckBoxes 미지원 → '선택' 컬럼에 ☑/☐ 를 직접 그린다.

namespace DocMine.UI.Tabs;

/// <summary>합집합 한 행. base(액션 가능) 또는 대조 전용(유령, 항상 비대상).</summary>
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
    private readonly RadioButton _tAll, _tDiff, _tBoth;   // 선택 대상
    private readonly CheckBox _hideNonTarget;             // 선택 대상만 보기
    private readonly Button _selAll, _selNone;
    private readonly Label _selInfo, _hint;

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
        bar.Controls.Add(new Label { Text = "선택 대상:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _tAll  = new RadioButton { Text = "전체", AutoSize = true, Checked = true, Margin = new Padding(0, 4, 0, 0) };
        _tDiff = new RadioButton { AutoSize = true, Enabled = false, Margin = new Padding(8, 4, 0, 0) };   // {compare}에 없는 것
        _tBoth = new RadioButton { Text = "양쪽에 있는 것", AutoSize = true, Enabled = false, Margin = new Padding(8, 4, 0, 0) };
        foreach (var rb in new[] { _tAll, _tDiff, _tBoth })
        {
            rb.CheckedChanged += (s, _) => { if (((RadioButton)s!).Checked) OnTargetChanged(); };
            bar.Controls.Add(rb);
        }
        _hideNonTarget = new CheckBox { Text = "선택 대상만 보기", AutoSize = true, Enabled = false, Margin = new Padding(12, 4, 0, 0) };
        _hideNonTarget.CheckedChanged += (_, _) => ApplyFilter();
        bar.Controls.Add(_hideNonTarget);

        _selAll  = new Button { Text = "전체 선택", AutoSize = true, Enabled = false, Margin = new Padding(16, 0, 0, 0) };
        _selNone = new Button { Text = "전체 해제", AutoSize = true, Enabled = false, Margin = new Padding(4, 0, 0, 0) };
        _selAll.Click  += (_, _) => SetAllChecked(true);
        _selNone.Click += (_, _) => SetAllChecked(false);
        bar.Controls.Add(_selAll);
        bar.Controls.Add(_selNone);
        _selInfo = new Label { Text = "0건 선택됨", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(12, 6, 0, 0) };
        bar.Controls.Add(_selInfo);
        _hint = new Label { Text = "(대상 아닌 행은 회색·선택 불가 · Shift+클릭=범위)", AutoSize = true, ForeColor = Color.DarkGray, Padding = new Padding(12, 6, 0, 0) };
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
        _dataColCount = dataColumns.Length;

        _list.Columns.Clear();
        _list.Columns.Add("선택", 44, HorizontalAlignment.Center);
        _list.Columns.Add(baseLabel, 64, HorizontalAlignment.Center);
        _compareCol = _list.Columns.Add(compareLabel, 0, HorizontalAlignment.Center);   // 로드 전 숨김
        foreach (var c in dataColumns)
            _list.Columns.Add(c.Header, c.Width, c.Align);

        _tDiff.Text = $"{compareLabel}에 없는 것";   // 차집합: InBase && !InCompare
    }

    /// <summary>행 공급 + 대조 로드 여부. 호출 시 선택 초기화.</summary>
    public void SetRows(IReadOnlyList<CompareRow> rows, bool compareLoaded)
    {
        _all.Clear();
        _all.AddRange(rows);
        _compareLoaded = compareLoaded;
        _compareCol.Width = compareLoaded ? 64 : 0;
        // 대조 미로드 → 차집합/교집합·숨김 의미 없음. 전체만.
        _tDiff.Enabled = compareLoaded;
        _tBoth.Enabled = compareLoaded;
        _hideNonTarget.Enabled = compareLoaded;
        if (!compareLoaded) { _tAll.Checked = true; _hideNonTarget.Checked = false; }
        _checked.Clear();
        _anchor = -1;
        ApplyFilter();
    }

    public IReadOnlyList<object> SelectedItems =>
        _all.Where(r => IsTarget(r) && r.Item is not null && _checked.Contains(r.Key))
            .Select(r => r.Item!).ToList();

    public int SelectedCount => _checked.Count;

    // 현재 라디오 기준 "선택 대상" 인 행인가. 유령(!InBase)은 절대 대상 아님.
    private bool IsTarget(CompareRow r)
    {
        if (!r.InBase) return false;
        if (!_compareLoaded || _tAll.Checked) return true;
        return _tDiff.Checked ? !r.InCompare : r.InCompare;   // 차집합 / 교집합
    }

    private void OnTargetChanged()
    {
        // 대상이 바뀌면 더는 대상이 아닌 선택은 정리.
        _checked.RemoveWhere(k => !_all.Any(r => r.Key == k && IsTarget(r)));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _view.Clear();
        foreach (var r in _all)
            if (!_hideNonTarget.Checked || IsTarget(r))   // 숨김 ON → 대상만
                _view.Add(r);

        _anchor = -1;
        _list.VirtualListSize = _view.Count;
        _list.Invalidate();

        var anyTarget = _all.Any(IsTarget);
        _selAll.Enabled = anyTarget;
        _selNone.Enabled = anyTarget;
        UpdateInfo();
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _view.Count) { e.Item = new ListViewItem(""); return; }
        var r = _view[e.ItemIndex];
        bool target = IsTarget(r);

        var cells = new string[3 + _dataColCount];
        cells[0] = target ? (_checked.Contains(r.Key) ? "☑" : "☐") : "";   // 비대상은 체크 칸 비움(잠금)
        cells[1] = r.InBase ? "✓" : "";
        cells[2] = _compareLoaded ? (r.InCompare ? "✓" : "") : "";
        for (int i = 0; i < _dataColCount; i++)
            cells[3 + i] = i < r.Cells.Length ? r.Cells[i] : "";

        var item = new ListViewItem(cells);
        if (!target) item.ForeColor = Color.Gray;   // 비대상(잠금/유령) 행 흐리게
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
            // 기준점에서 한 동작(체크/해제)을 범위 전체에 적용. 비대상 행은 건너뜀.
            var lo = Math.Min(_anchor, idx);
            var hi = Math.Max(_anchor, idx);
            for (var i = lo; i <= hi; i++)
            {
                var rr = _view[i];
                if (!IsTarget(rr)) continue;
                if (_anchorChecked) _checked.Add(rr.Key); else _checked.Remove(rr.Key);
            }
            _list.Invalidate();
        }
        else
        {
            var r = _view[idx];
            if (!IsTarget(r)) return;   // 비대상 행은 선택 불가
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
                if (IsTarget(r)) _checked.Add(r.Key);
        _list.Invalidate();
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        _selInfo.Text = $"{_checked.Count:N0}건 선택됨 (표시 {_view.Count:N0})";
        SelectionChanged?.Invoke();
    }
}
