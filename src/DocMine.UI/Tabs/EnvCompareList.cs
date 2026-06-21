// EnvCompareList — 두 환경(현재 ↔ 대조본)의 합집합 대조 리스트 (반출·반입 공유).
//
// 화면은 *항상 전부* 보여준다(두 presence 컬럼). 숨기거나 잠그지 않는다 —
// "이미 넘어간 것" 옆에 있어야 신규가 완전 새것인지 마이너 업데이트인지 판단할 수 있어서.
// 선택은 모드가 아니라 명시적 버튼으로:
//   [{compare}에 없는 것]  = 미전송/신규 한 번에 선택
//   [{compare}에 있는 것]  = 양쪽 공통(예: 삭제 후보) 선택
//   [전체] / [해제]
// 행을 직접 클릭(토글)·Shift+클릭(범위)으로 세부 조정도 가능. 유령(대조 전용) 행은 선택 불가.
//
// WinForms VirtualMode 는 CheckBoxes 미지원 → '선택' 컬럼에 ☑/☐ 를 직접 그린다.

namespace DocMine.UI.Tabs;

/// <summary>합집합 한 행. base(선택 가능) 또는 대조 전용(유령, 선택 불가).</summary>
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
    private readonly Button _selNotInCompare, _selInCompare, _selAll, _selNone;
    private readonly Label _selInfo, _hint;

    private ColumnHeader _compareCol = null!;
    private int _dataColCount;
    private bool _compareLoaded;

    private readonly List<CompareRow> _rows = new();
    private readonly HashSet<(string, string)> _checked = new();
    private int _anchor = -1;
    private bool _anchorChecked;

    public event Action? SelectionChanged;

    public EnvCompareList()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 4) };
        bar.Controls.Add(new Label { Text = "선택:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _selNotInCompare = new Button { AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 4, 0) };   // {compare}에 없는 것
        _selInCompare    = new Button { AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 4, 0) };   // {compare}에 있는 것
        _selAll          = new Button { Text = "전체", AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 4, 0) };
        _selNone         = new Button { Text = "해제", AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 0, 0) };
        _selNotInCompare.Click += (_, _) => SelectGroup(r => r.InBase && !r.InCompare);
        _selInCompare.Click    += (_, _) => SelectGroup(r => r.InBase && r.InCompare);
        _selAll.Click          += (_, _) => SelectGroup(r => r.InBase);
        _selNone.Click         += (_, _) => SelectGroup(_ => false);
        bar.Controls.Add(_selNotInCompare);
        bar.Controls.Add(_selInCompare);
        bar.Controls.Add(_selAll);
        bar.Controls.Add(_selNone);
        _selInfo = new Label { Text = "0건 선택됨", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(12, 6, 0, 0) };
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

        _selNotInCompare.Text = $"{compareLabel}에 없는 것";   // 미전송/신규
        _selInCompare.Text    = $"{compareLabel}에 있는 것";   // 양쪽 공통
    }

    /// <summary>행 공급 + 대조 로드 여부. 호출 시 선택 초기화.</summary>
    public void SetRows(IReadOnlyList<CompareRow> rows, bool compareLoaded)
    {
        _rows.Clear();
        _rows.AddRange(rows);
        _compareLoaded = compareLoaded;
        _compareCol.Width = compareLoaded ? 64 : 0;

        var anyBase = _rows.Any(r => r.InBase);
        _selNotInCompare.Enabled = compareLoaded && anyBase;   // 대조 없으면 의미 없음
        _selInCompare.Enabled    = compareLoaded && anyBase;
        _selAll.Enabled  = anyBase;
        _selNone.Enabled = anyBase;

        _checked.Clear();
        _anchor = -1;
        _list.VirtualListSize = _rows.Count;
        _list.Invalidate();
        UpdateInfo();
    }

    public IReadOnlyList<object> SelectedItems =>
        _rows.Where(r => r.InBase && r.Item is not null && _checked.Contains(r.Key))
             .Select(r => r.Item!).ToList();

    public int SelectedCount => _checked.Count;

    // 조건에 맞는 base 행으로 선택을 '교체'(누적 아님 — "이 그룹을 선택").
    private void SelectGroup(Func<CompareRow, bool> predicate)
    {
        _checked.Clear();
        foreach (var r in _rows)
            if (r.InBase && predicate(r)) _checked.Add(r.Key);
        _list.Invalidate();
        UpdateInfo();
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count) { e.Item = new ListViewItem(""); return; }
        var r = _rows[e.ItemIndex];

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
        if (idx < 0 || idx >= _rows.Count) return;

        if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift && _anchor >= 0 && _anchor < _rows.Count)
        {
            // 기준점에서 한 동작(체크/해제)을 범위 전체에 적용. 유령 행은 건너뜀.
            var lo = Math.Min(_anchor, idx);
            var hi = Math.Max(_anchor, idx);
            for (var i = lo; i <= hi; i++)
            {
                var rr = _rows[i];
                if (!rr.InBase) continue;
                if (_anchorChecked) _checked.Add(rr.Key); else _checked.Remove(rr.Key);
            }
            _list.Invalidate();
        }
        else
        {
            var r = _rows[idx];
            if (!r.InBase) return;   // 유령 행은 선택 불가
            _anchorChecked = _checked.Add(r.Key);
            if (!_anchorChecked) _checked.Remove(r.Key);
            _anchor = idx;
            _list.Invalidate(hit.Item.Bounds);
        }
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        _selInfo.Text = $"{_checked.Count:N0}건 선택됨 (표시 {_rows.Count:N0})";
        SelectionChanged?.Invoke();
    }
}
