// EnvCompareList — 현재 목록(base) 을 기준으로, 대조본 보유 여부를 한 컬럼으로 표시 (반출·반입 공유).
//
// base 가 *항상 기준*이다 — base 의 전체를 늘 보여준다. 대조본(매니페스트/기적재 등)에만
// 있고 base 엔 없는 항목은 표시하지 않는다(유령 행 없음). 대조본 보유 여부만 ✓ 로 표기.
//
// 버튼 3개:  [전체 선택]  [전체 해제]  [{compare}에 없는 것만 선택]
//   마지막 버튼이 핵심 — "아직 안 넘어간 신규만" 한 번에 선택. (대조 로드 시에만 표시)
// 행 클릭=토글, Shift+클릭=범위로 세부 조정.
//
// WinForms VirtualMode 는 CheckBoxes 미지원 → '선택' 컬럼에 ☑/☐ 를 직접 그린다.

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
    public readonly record struct ColumnDef(string Header, int Width, HorizontalAlignment Align);

    private readonly ListView _list;
    private readonly Button _selAll, _selNone, _selNotInCompare;
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
        _selAll          = new Button { Text = "전체 선택", AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 4, 0) };
        _selNone         = new Button { Text = "전체 해제", AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 4, 0) };
        _selNotInCompare = new Button { AutoSize = true, Enabled = false, Visible = false, Margin = new Padding(0, 0, 0, 0) };  // {compare}에 없는 것만
        _selAll.Click          += (_, _) => SelectGroup(_ => true);
        _selNone.Click         += (_, _) => SelectGroup(_ => false);
        _selNotInCompare.Click += (_, _) => SelectGroup(r => !r.InCompare);
        bar.Controls.Add(_selAll);
        bar.Controls.Add(_selNone);
        bar.Controls.Add(_selNotInCompare);
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

    /// <summary>컬럼 구성 — 한 번만 호출. 선택 + 대조(✓) 뒤에 dataColumns 가 붙는다.</summary>
    public void Configure(string compareLabel, params ColumnDef[] dataColumns)
    {
        _dataColCount = dataColumns.Length;

        _list.Columns.Clear();
        _list.Columns.Add("선택", 44, HorizontalAlignment.Center);
        _compareCol = _list.Columns.Add(compareLabel, 0, HorizontalAlignment.Center);   // 대조 로드 전 숨김
        foreach (var c in dataColumns)
            _list.Columns.Add(c.Header, c.Width, c.Align);

        _selNotInCompare.Text = $"{compareLabel}에 없는 것만 선택";
    }

    /// <summary>행 공급 + 대조 로드 여부. 호출 시 선택 초기화.</summary>
    public void SetRows(IReadOnlyList<CompareRow> rows, bool compareLoaded)
    {
        _rows.Clear();
        _rows.AddRange(rows);
        _compareLoaded = compareLoaded;
        _compareCol.Width = compareLoaded ? 64 : 0;

        var any = _rows.Count > 0;
        _selAll.Enabled = any;
        _selNone.Enabled = any;
        _selNotInCompare.Visible = compareLoaded;
        _selNotInCompare.Enabled = compareLoaded && any;

        _checked.Clear();
        _anchor = -1;
        _list.VirtualListSize = _rows.Count;
        _list.Invalidate();
        UpdateInfo();
    }

    public IReadOnlyList<object> SelectedItems =>
        _rows.Where(r => r.Item is not null && _checked.Contains(r.Key)).Select(r => r.Item!).ToList();

    public int SelectedCount => _checked.Count;

    // 조건에 맞는 행으로 선택을 '교체'(누적 아님 — "이 그룹을 선택").
    private void SelectGroup(Func<CompareRow, bool> predicate)
    {
        _checked.Clear();
        foreach (var r in _rows)
            if (predicate(r)) _checked.Add(r.Key);
        _list.Invalidate();
        UpdateInfo();
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count) { e.Item = new ListViewItem(""); return; }
        var r = _rows[e.ItemIndex];

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
        if (idx < 0 || idx >= _rows.Count) return;

        if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift && _anchor >= 0 && _anchor < _rows.Count)
        {
            // 기준점에서 한 동작(체크/해제)을 범위 전체에 적용.
            var lo = Math.Min(_anchor, idx);
            var hi = Math.Max(_anchor, idx);
            for (var i = lo; i <= hi; i++)
            {
                var key = _rows[i].Key;
                if (_anchorChecked) _checked.Add(key); else _checked.Remove(key);
            }
            _list.Invalidate();
        }
        else
        {
            var key = _rows[idx].Key;
            _anchorChecked = _checked.Add(key);
            if (!_anchorChecked) _checked.Remove(key);
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
