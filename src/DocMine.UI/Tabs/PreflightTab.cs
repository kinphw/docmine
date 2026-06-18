// PreflightTab — 적재 전 검증.
//
// ①스캔이 만든 CSV(.csd)를 현재 DB 적재 현황과 대조해, '아직 적재 안 된 파일'을
// 적재 전에 미리 확인. 매칭은 적재 단계의 skip 판정과 동일한 LoadExistingKeys
// (NormKey: NFC+trim+lowercase) — 즉 여기서 '미적재'로 뜨는 게 실제 적재 대상.
//
// 환경2 manifest 대조(⑤ DB 추출)와는 다른 축:
//   ⑤        : DB(환경2) → 추출 대상 / manifest 로 환경2 기적재 대조
//   적재 전 검증: 스캔 CSV(파일시스템) → 적재 대상 / 현재 DB 로 기적재 대조
//
// UX 는 ③검색·⑤추출과 동일한 ListView 가상화 (전체 로드 + 보이는 행만 렌더).

using DocMine.Core.Config;
using DocMine.Core.Db;
using DocMine.Core.Pipeline;

namespace DocMine.UI.Tabs;

public sealed class PreflightTab : TabPage
{
    private sealed record Row(CsvRow Csv, bool Loaded);

    private DocumentRepository _repo;
    private readonly TextBox _csvBox;
    private readonly Button _verifyBtn;
    private readonly Label _statusLabel;
    private readonly CheckBox _unloadedOnlyBox;
    private readonly ListView _list;
    private readonly LogPane _log;
    private readonly Font _boldFont;

    // 전체(CSV 행 + 적재 여부) / 필터 적용 표시 대상.
    private readonly List<Row> _all = new();
    private readonly List<Row> _viewRows = new();

    public PreflightTab() : base("적재 전 검증")
    {
        var cfg = AppConfig.Current;
        _repo = new DocumentRepository(cfg);
        _boldFont = new Font("맑은 고딕", 9, FontStyle.Bold);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var info = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Gray,
            Text = "①스캔 결과 CSV(.csd)를 현재 DB 와 대조해 '아직 적재 안 된 파일'을 확인합니다.\n" +
                   "여기서 '미적재'로 뜨는 항목이 다음 적재의 실제 대상입니다.",
        };

        // ── 상단: CSV 선택 + 검증 + 통계 ──────────────────────────────
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 4) };
        top.Controls.Add(new Label { Text = "입력 CSV (스캔 결과):", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _csvBox = new TextBox { Width = 320 };
        top.Controls.Add(_csvBox);
        var browseBtn = new Button { Text = "찾아보기…", AutoSize = true, Margin = new Padding(4, 2, 0, 0) };
        browseBtn.Click += (_, _) => BrowseCsv();
        top.Controls.Add(browseBtn);
        _verifyBtn = new Button { Text = "검증", AutoSize = true, Margin = new Padding(4, 2, 0, 0) };
        _verifyBtn.Click += async (_, _) => await VerifyAsync();
        top.Controls.Add(_verifyBtn);
        _unloadedOnlyBox = new CheckBox { Text = "미적재만 표시", AutoSize = true, Margin = new Padding(12, 4, 0, 0) };
        _unloadedOnlyBox.CheckedChanged += (_, _) => ApplyFilter();
        top.Controls.Add(_unloadedOnlyBox);
        _statusLabel = new Label { Text = "", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(12, 6, 0, 0) };
        top.Controls.Add(_statusLabel);

        var topContainer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        topContainer.Controls.Add(info, 0, 0);
        topContainer.Controls.Add(top, 0, 1);
        root.Controls.Add(topContainer, 0, 0);

        // ── 결과 ListView (가상화) ────────────────────────────────────
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            GridLines = false,
            Font = new Font("맑은 고딕", 9),
            VirtualMode = true,
        };
        _list.Columns.Add("적재",    50, HorizontalAlignment.Center);
        _list.Columns.Add("폴더",   320);
        _list.Columns.Add("파일명", 260);
        _list.Columns.Add("확장자",  60);
        _list.Columns.Add("크기",    90, HorizontalAlignment.Right);
        _list.Columns.Add("수정일", 140);
        _list.RetrieveVirtualItem += OnRetrieveItem;
        root.Controls.Add(_list, 0, 1);

        // ── 로그 ──────────────────────────────────────────────────────
        _log = new LogPane { Dock = DockStyle.Fill };
        var logFrame = new GroupBox { Text = "로그", Dock = DockStyle.Fill, Height = 90, Padding = new Padding(4) };
        logFrame.Controls.Add(_log);
        root.Controls.Add(logFrame, 0, 2);

        Controls.Add(root);

        // 탭 활성화 시: 설정 테이블 변경 반영 + 입력칸 비었으면 최근 스캔 CSV 제안.
        VisibleChanged += (_, _) =>
        {
            if (!Visible) return;
            _repo = new DocumentRepository(AppConfig.Current);
            if (string.IsNullOrEmpty(_csvBox.Text))
            {
                var last = ScanResultRegistry.HwpLast ?? ScanResultRegistry.PdfLast;
                if (last is not null) _csvBox.Text = last;
            }
        };
    }

    private void BrowseCsv()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "스캔 결과 CSV 선택",
            Filter = "CSV (*.csd;*.csv)|*.csd;*.csv|모든 파일 (*.*)|*.*",
        };
        if (!string.IsNullOrEmpty(_csvBox.Text))
            dlg.FileName = Path.GetFileName(_csvBox.Text);
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _csvBox.Text = dlg.FileName;
    }

    private async Task VerifyAsync()
    {
        var csv = _csvBox.Text.Trim();
        if (string.IsNullOrEmpty(csv) || !File.Exists(csv))
        {
            MessageBox.Show(this, "스캔 결과 CSV 파일을 선택하세요.", "파일 없음");
            return;
        }

        _verifyBtn.Enabled = false;
        _statusLabel.Text = "검증 중...";
        try
        {
            var cfg = AppConfig.Current;
            var rows = await Task.Run(() =>
            {
                var csvRows = CsvIngestHelpers.LoadCsv(csv);
                // 적재 단계의 skip 판정과 동일 — 현재 DB 에 있는 (dir,filename) 키 집합.
                var existing = CsvIngestHelpers.LoadExistingKeys(cfg, _repo, csvRows);
                return csvRows.Select(r => new Row(
                    r, existing.Contains(CsvIngestHelpers.NormKey(r.Directory, r.Filename)))).ToList();
            });

            _all.Clear();
            _all.AddRange(rows);
            ApplyFilter();

            var total = _all.Count;
            var loaded = _all.Count(r => r.Loaded);
            var unloaded = total - loaded;
            _log.AppendLine($"[검증] {Path.GetFileName(csv)} | table={cfg.DbTable} → 전체 {total:N0} · 적재됨 {loaded:N0} · 미적재 {unloaded:N0}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "검증 오류");
            _statusLabel.Text = "오류";
            _log.AppendLine($"[오류] {ex.Message}");
        }
        finally
        {
            _verifyBtn.Enabled = true;
        }
    }

    private void ApplyFilter()
    {
        _viewRows.Clear();
        foreach (var r in _all)
            if (!_unloadedOnlyBox.Checked || !r.Loaded) _viewRows.Add(r);

        _list.VirtualListSize = _viewRows.Count;
        _list.Invalidate();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var total = _all.Count;
        var loaded = _all.Count(r => r.Loaded);
        var unloaded = total - loaded;
        var filterNote = _unloadedOnlyBox.Checked ? $"  (미적재 {unloaded:N0}건만 표시)" : "";
        _statusLabel.Text = $"전체 {total:N0} · 적재됨 {loaded:N0} · 미적재 {unloaded:N0}{filterNote}";
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _viewRows.Count)
        {
            e.Item = new ListViewItem("");
            return;
        }
        var row = _viewRows[e.ItemIndex];
        var c = row.Csv;
        var sizeStr = c.SizeBytes >= 1024 * 1024
            ? $"{c.SizeBytes / 1024.0 / 1024.0:F1} MB"
            : $"{c.SizeBytes / 1024.0:F0} KB";
        // subitem 순서 = 컬럼 순서 (적재, 폴더, 파일명, 확장자, 크기, 수정일).
        var item = new ListViewItem(new[]
        {
            row.Loaded ? "✓" : "", c.Directory, c.Filename, c.Extension, sizeStr, c.Modified,
        });
        // 미적재(적재 대상) 행을 굵게 강조.
        if (!row.Loaded) item.Font = _boldFont;
        e.Item = item;
    }
}
