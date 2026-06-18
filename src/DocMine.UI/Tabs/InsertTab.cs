// InsertTab — 검증 + HWP/PDF 통합 적재 탭.
//
// 단일 CSV(①스캔 결과)를 입력으로:
//   1) [검증] — CSV ↔ 현재 DB 대조해 '미적재' 파일을 가상 리스트로 확인.
//   2) [미적재 전체 적재] — CSV 전체를 Runner 에 넘김(이미 적재된 건 자동 skip).
//      검증 없이도 동작 — 기존 적재와 동일.
//   3) [선택 항목 적재] — 검증 리스트에서 체크한 행만 임시 CSV 로 추려 적재.
//
// 대상(HWP/PDF) 체크박스로 선택. 각 Runner 가 자기 확장자만 필터링하므로
// 단일 CSV 를 공유하고, 순차 적재(HWP→PDF). 한 대상 실패해도 다음 계속.

using DocMine.Core.Config;
using DocMine.Core.Db;
using DocMine.Core.Pipeline;
using DocMine.Core.Scanning;

namespace DocMine.UI.Tabs;

public sealed class InsertTab : TabPage, IBusyTab
{
    private sealed record Row(CsvRow Csv, bool Loaded);

    private DocumentRepository _repo;
    private readonly TextBox _csvBox;
    private readonly LinkLabel _useLastScanLink;
    private readonly CheckBox _hwpBox;
    private readonly CheckBox _pdfBox;
    private readonly Button _verifyBtn;
    private readonly Label _statusLabel;
    private readonly CheckBox _unloadedOnlyBox;

    private readonly ListView _list;
    private readonly Button _insertAllBtn;
    private readonly Button _insertSelBtn;
    private readonly Button _selectAllBtn;
    private readonly Button _selectNoneBtn;
    private readonly Button _stopBtn;
    private readonly Label _selInfoLabel;

    private readonly LogPane _log;
    private readonly ProgressEta _eta = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    // 검증 결과 + 선택 상태.
    private readonly List<Row> _all = new();
    private readonly List<Row> _viewRows = new();
    private readonly HashSet<(string, string)> _checkedKeys = new();   // NormKey 로 선택 추적

    public InsertTab() : base("② 적재")
    {
        _repo = new DocumentRepository(AppConfig.Current);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 입력/대상/검증
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 검증 리스트
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 적재 버튼
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 로그

        // ── 상단 (입력 CSV + 대상 + 검증) ────────────────────────────
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 4 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var info = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Gray,
            Text = "①스캔 결과 CSV 의 문서를 documents 테이블에 적재합니다. 이미 적재된 파일은 자동 건너뜁니다.\n" +
                   "[검증]으로 미적재 목록을 확인하고, 전체 또는 선택 항목만 적재할 수 있습니다.",
        };
        top.Controls.Add(info, 0, 0);

        // 입력 CSV.
        var csvRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        csvRow.Controls.Add(new Label { Text = "입력 CSV:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _csvBox = new TextBox { Width = 360, Text = Path.GetFullPath(AppConfig.DefaultScanCsv) };
        csvRow.Controls.Add(_csvBox);
        var browseBtn = new Button { Text = "찾아보기…", AutoSize = true, Margin = new Padding(4, 2, 0, 0) };
        browseBtn.Click += (_, _) => BrowseCsv();
        csvRow.Controls.Add(browseBtn);
        _useLastScanLink = new LinkLabel { AutoSize = true, Visible = false, Padding = new Padding(8, 6, 0, 0), LinkColor = Color.SteelBlue };
        _useLastScanLink.LinkClicked += (_, _) => UseLastScan();
        csvRow.Controls.Add(_useLastScanLink);
        top.Controls.Add(csvRow, 0, 1);

        // 대상.
        var tgtRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        tgtRow.Controls.Add(new Label { Text = "적재 대상:", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        _hwpBox = new CheckBox { Text = "HWP (.hwp/.hwpx)", Checked = true, AutoSize = true, Margin = new Padding(0, 2, 16, 0) };
        _pdfBox = new CheckBox { Text = "PDF (.pdf)", Checked = true, AutoSize = true };
        tgtRow.Controls.Add(_hwpBox);
        tgtRow.Controls.Add(_pdfBox);
        top.Controls.Add(tgtRow, 0, 2);

        // 검증 + 통계 + 미적재만.
        var verifyRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        _verifyBtn = new Button { Text = "검증 (미적재 확인)", AutoSize = true };
        _verifyBtn.Click += async (_, _) => await VerifyAsync();
        verifyRow.Controls.Add(_verifyBtn);
        _unloadedOnlyBox = new CheckBox { Text = "미적재만 표시", AutoSize = true, Margin = new Padding(12, 4, 0, 0) };
        _unloadedOnlyBox.CheckedChanged += (_, _) => ApplyFilter();
        verifyRow.Controls.Add(_unloadedOnlyBox);
        _statusLabel = new Label { Text = "", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(12, 6, 0, 0) };
        verifyRow.Controls.Add(_statusLabel);
        top.Controls.Add(verifyRow, 0, 3);

        root.Controls.Add(top, 0, 0);

        // ── 검증 리스트 (가상화 + 의사 체크박스) ─────────────────────
        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            HideSelection = false, MultiSelect = false, GridLines = false,
            Font = new Font("맑은 고딕", 9), VirtualMode = true,
        };
        _list.Columns.Add("선택",   44, HorizontalAlignment.Center);
        _list.Columns.Add("적재",   44, HorizontalAlignment.Center);
        _list.Columns.Add("폴더",  300);
        _list.Columns.Add("파일명", 240);
        _list.Columns.Add("확장자",  60);
        _list.Columns.Add("크기",    90, HorizontalAlignment.Right);
        _list.Columns.Add("수정일", 140);
        _list.RetrieveVirtualItem += OnRetrieveItem;
        _list.MouseClick += OnListMouseClick;
        root.Controls.Add(_list, 0, 1);

        // ── 적재 버튼 ────────────────────────────────────────────────
        var actRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 4, 0, 4) };
        actRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _selInfoLabel = new Label { Text = "0건 선택됨", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(0, 8, 0, 0) };
        actRow.Controls.Add(_selInfoLabel, 0, 0);

        var actFlow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _stopBtn        = new Button { Text = "중지", AutoSize = true, Enabled = false, Margin = new Padding(6, 0, 0, 0) };
        _insertSelBtn   = new Button { Text = "선택 항목 적재", AutoSize = true, Enabled = false };
        _insertAllBtn   = new Button { Text = "미적재 전체 적재", AutoSize = true };
        _selectNoneBtn  = new Button { Text = "전체 해제", AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 12, 0) };
        _selectAllBtn   = new Button { Text = "전체 선택", AutoSize = true, Enabled = false };
        _stopBtn.Click       += (_, _) => OnStop();
        _insertSelBtn.Click  += async (_, _) => await InsertSelectedAsync();
        _insertAllBtn.Click  += async (_, _) => await InsertAllAsync();
        _selectNoneBtn.Click += (_, _) => SetAllChecked(false);
        _selectAllBtn.Click  += (_, _) => SetAllChecked(true);
        // RightToLeft — Add 역순 배치.
        actFlow.Controls.Add(_stopBtn);
        actFlow.Controls.Add(_insertSelBtn);
        actFlow.Controls.Add(_insertAllBtn);
        actFlow.Controls.Add(_selectNoneBtn);
        actFlow.Controls.Add(_selectAllBtn);
        actRow.Controls.Add(actFlow, 1, 0);
        root.Controls.Add(actRow, 0, 2);

        // ── 로그 ──────────────────────────────────────────────────────
        _log = new LogPane { Dock = DockStyle.Fill };
        var logFrame = new GroupBox { Text = "로그", Dock = DockStyle.Fill, Height = 150, Padding = new Padding(4) };
        logFrame.Controls.Add(_log);
        root.Controls.Add(logFrame, 0, 3);

        Controls.Add(root);

        VisibleChanged += (_, _) =>
        {
            if (!Visible) return;
            _repo = new DocumentRepository(AppConfig.Current);
            RefreshUseLastScanLink();
        };
    }

    private void BrowseCsv()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "스캔 결과 CSV 선택",
            FileName = Path.GetFileName(_csvBox.Text),
            Filter = "CSV (*.csd;*.csv)|*.csd;*.csv|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _csvBox.Text = dlg.FileName;
    }

    // ─ 검증 ──────────────────────────────────────────────────────────
    private async Task VerifyAsync()
    {
        if (_busy) return;
        var csv = _csvBox.Text.Trim();
        if (string.IsNullOrEmpty(csv) || !File.Exists(csv))
        {
            MessageBox.Show(this, $"CSV 파일을 찾을 수 없습니다:\n{csv}", "CSV 없음");
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
                var existing = CsvIngestHelpers.LoadExistingKeys(cfg, _repo, csvRows);
                return csvRows.Select(r => new Row(
                    r, existing.Contains(CsvIngestHelpers.NormKey(r.Directory, r.Filename)))).ToList();
            });

            _all.Clear();
            _all.AddRange(rows);
            _checkedKeys.Clear();
            ApplyFilter();

            var total = _all.Count;
            var loaded = _all.Count(r => r.Loaded);
            _log.AppendLine($"[검증] {Path.GetFileName(csv)} | table={cfg.DbTable} → 전체 {total:N0} · 적재됨 {loaded:N0} · 미적재 {total - loaded:N0}");
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

        var any = _viewRows.Count > 0;
        _selectAllBtn.Enabled = any && !_busy;
        _selectNoneBtn.Enabled = any && !_busy;
        UpdateStatusLabel();
        UpdateSelInfo();
    }

    private void UpdateStatusLabel()
    {
        var total = _all.Count;
        var loaded = _all.Count(r => r.Loaded);
        var note = _unloadedOnlyBox.Checked ? "  (미적재만 표시)" : "";
        _statusLabel.Text = total == 0 ? "" : $"전체 {total:N0} · 적재됨 {loaded:N0} · 미적재 {total - loaded:N0}{note}";
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _viewRows.Count) { e.Item = new ListViewItem(""); return; }
        var row = _viewRows[e.ItemIndex];
        var c = row.Csv;
        var sizeStr = c.SizeBytes >= 1024 * 1024 ? $"{c.SizeBytes / 1024.0 / 1024.0:F1} MB" : $"{c.SizeBytes / 1024.0:F0} KB";
        var key = CsvIngestHelpers.NormKey(c.Directory, c.Filename);
        // subitem 순서 = 컬럼 (선택, 적재, 폴더, 파일명, 확장자, 크기, 수정일).
        e.Item = new ListViewItem(new[]
        {
            _checkedKeys.Contains(key) ? "☑" : "☐",
            row.Loaded ? "✓" : "",
            c.Directory, c.Filename, c.Extension, sizeStr, c.Modified,
        });
    }

    private void OnListMouseClick(object? sender, MouseEventArgs e)
    {
        if (_busy) return;
        var hit = _list.HitTest(e.Location);
        if (hit.Item is null) return;
        var idx = hit.Item.Index;
        if (idx < 0 || idx >= _viewRows.Count) return;
        var c = _viewRows[idx].Csv;
        var key = CsvIngestHelpers.NormKey(c.Directory, c.Filename);
        if (!_checkedKeys.Add(key)) _checkedKeys.Remove(key);   // 토글
        _list.Invalidate(hit.Item.Bounds);
        UpdateSelInfo();
    }

    private void SetAllChecked(bool value)
    {
        _checkedKeys.Clear();
        if (value)
            foreach (var r in _viewRows)
                _checkedKeys.Add(CsvIngestHelpers.NormKey(r.Csv.Directory, r.Csv.Filename));
        _list.Invalidate();
        UpdateSelInfo();
    }

    private void UpdateSelInfo()
    {
        var n = _checkedKeys.Count;
        _selInfoLabel.Text = $"{n:N0}건 선택됨 (표시 {_viewRows.Count:N0}건)";
        _insertSelBtn.Enabled = n > 0 && !_busy;
    }

    // ─ 적재 ──────────────────────────────────────────────────────────
    private async Task InsertAllAsync()
    {
        // CSV 전체를 Runner 에 — 이미 적재된 건 자동 skip(= 미적재만 적재).
        await RunInsertAsync(_csvBox.Text.Trim(), tempCsv: false);
    }

    private async Task InsertSelectedAsync()
    {
        var selected = _all
            .Where(r => _checkedKeys.Contains(CsvIngestHelpers.NormKey(r.Csv.Directory, r.Csv.Filename)))
            .Select(r => r.Csv).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "적재할 항목을 선택하세요. ([검증] 후 행을 클릭하면 ☑)", "선택 없음");
            return;
        }
        // 선택 행만 임시 CSV 로 추려 Runner 에 넘긴다. (Runner 는 경로 입력이라
        // 행 단위 필터를 위해 임시 파일 사용 — 적재 후 삭제.)
        var temp = Path.Combine(Path.GetTempPath(), $"docmine_sel_{Guid.NewGuid():N}.csd");
        DriveScanner.WriteCsv(
            selected.Select(c => new ScannedFile(c.Directory, c.Filename, c.Extension, c.SizeBytes, c.Modified)),
            temp);
        try { await RunInsertAsync(temp, tempCsv: true, selectedCount: selected.Count); }
        finally { try { File.Delete(temp); } catch { } }
    }

    private async Task RunInsertAsync(string csv, bool tempCsv, int selectedCount = 0)
    {
        if (_busy) return;
        if (string.IsNullOrEmpty(csv) || !File.Exists(csv))
        {
            MessageBox.Show(this, $"CSV 파일을 찾을 수 없습니다:\n{csv}", "CSV 없음");
            return;
        }
        if (!_hwpBox.Checked && !_pdfBox.Checked)
        {
            MessageBox.Show(this, "적재 대상(HWP/PDF)을 하나 이상 선택하세요.", "대상 선택");
            return;
        }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        _log.Clear();

        var cfg = AppConfig.Current;
        _log.AppendLine($"  DB: {cfg.DbUser}@{cfg.DbHost}:{cfg.DbPort}/{cfg.DbName} (table={cfg.DbTable})");
        _log.AppendLine(tempCsv
            ? $"  모드: 선택 항목 적재 ({selectedCount:N0}건)"
            : "  모드: 미적재 전체 적재 (이미 적재된 건 skip)");
        _log.AppendLine($"  대상: {(_hwpBox.Checked ? "HWP " : "")}{(_pdfBox.Checked ? "PDF" : "")}".TrimEnd());

        var token = _cts.Token;
        try
        {
            if (_hwpBox.Checked)
            {
                _log.AppendLine("\n  ── HWP 적재 ──");
                _eta.Reset();
                var runner = new HwpInsertRunner(cfg);
                await Task.Run(() => runner.RunAsync(csv, 0, null,
                    onLog: line => _log.AppendLine(line), onProgress: OnHwpProgress,
                    cancellationToken: token), token);
            }
            if (_pdfBox.Checked)
            {
                _log.AppendLine("\n  ── PDF 적재 ──");
                _eta.Reset();
                var runner = new PdfInsertRunner(cfg);
                await Task.Run(() => runner.RunAsync(csv, 0, null,
                    onLog: line => _log.AppendLine(line), onProgress: OnPdfProgress,
                    cancellationToken: token), token);
            }
            _log.AppendLine("\n  전체 적재 완료.");
        }
        catch (OperationCanceledException) { _log.AppendLine("\n  중단됨."); }
        catch (Exception ex) { _log.AppendLine($"\n[오류] {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnHwpProgress(HwpInsertProgress p)
    {
        var pct = p.Total == 0 ? 100 : p.Index * 100.0 / p.Total;
        var crash = p.Crash > 0 ? $" crash:{p.Crash}" : "";
        var skip  = p.Skip  > 0 ? $" skip:{p.Skip}"   : "";
        _log.UpdateLive($"  [HWP {ProgressBar(pct, 30)}] {p.Index}/{p.Total}  ok:{p.Ok} err:{p.Err}{crash}{skip}{_eta.Format(p.Index, p.Total)}");
    }

    private void OnPdfProgress(PdfInsertProgress p)
    {
        var pct = p.Total == 0 ? 100 : p.Index * 100.0 / p.Total;
        var empty = p.Empty > 0 ? $" empty:{p.Empty}" : "";
        var skip  = p.Skip  > 0 ? $" skip:{p.Skip}"   : "";
        _log.UpdateLive($"  [PDF {ProgressBar(pct, 30)}] {p.Index}/{p.Total}  ok:{p.Ok} err:{p.Err}{empty}{skip}{_eta.Format(p.Index, p.Total)}");
    }

    private void OnStop()
    {
        if (!_busy) return;
        _cts?.Cancel();
        _stopBtn.Enabled = false;
        _stopBtn.Text = "중지 중…";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _verifyBtn.Enabled    = !busy;
        _insertAllBtn.Enabled = !busy;
        _insertSelBtn.Enabled = !busy && _checkedKeys.Count > 0;
        _selectAllBtn.Enabled = !busy && _viewRows.Count > 0;
        _selectNoneBtn.Enabled = !busy && _viewRows.Count > 0;
        _stopBtn.Enabled = busy;
        _stopBtn.Text = "중지";
        _insertAllBtn.Text = busy ? "적재 중…" : "미적재 전체 적재";
    }

    private static string ProgressBar(double pct, int width)
    {
        var n = Math.Clamp((int)Math.Round(pct / 100.0 * width), 0, width);
        return new string('#', n) + new string('.', width - n);
    }

    // ─ 최근 스캔 핸드오프 ─────────────────────────────────────────────
    private void RefreshUseLastScanLink()
    {
        var last = ScanResultRegistry.LastScanCsv;
        if (string.IsNullOrEmpty(last)) { _useLastScanLink.Visible = false; return; }
        string current;
        try { current = Path.GetFullPath(_csvBox.Text.Trim()); } catch { current = ""; }
        if (string.Equals(last, current, StringComparison.OrdinalIgnoreCase)) { _useLastScanLink.Visible = false; return; }
        _useLastScanLink.Text = $"↻ 최근 스캔 결과 사용: {Path.GetFileName(last)}";
        _useLastScanLink.Visible = true;
    }

    private void UseLastScan()
    {
        var last = ScanResultRegistry.LastScanCsv;
        if (string.IsNullOrEmpty(last)) return;
        _csvBox.Text = last;
        _useLastScanLink.Visible = false;
    }

    // ─ IBusyTab ─────────────────────────────────────────────────────
    public bool IsBusy => _busy;
    public void RequestStop() => OnStop();
}
