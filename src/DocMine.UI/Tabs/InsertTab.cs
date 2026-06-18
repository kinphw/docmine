// InsertTab — HWP + PDF 통합 적재 탭.
//
// 단일 CSV(①스캔 결과)를 입력으로 받아, 체크된 대상(HWP/PDF)을 순차 적재.
//   - HWP : HwpInsertRunner (한/글 COM 워커) — .hwp/.hwpx 만 처리
//   - PDF : PdfInsertRunner (PdfPig 워커)    — .pdf 만 처리
// 각 Runner 가 LoadCsv 후 자기 확장자만 필터링하므로 같은 CSV 를 공유한다.
// 한 대상이 실패해도(예: 한/글 미설치) 다음 대상은 계속 — 부분 실패 격리.

using DocMine.Core.Config;
using DocMine.Core.Pipeline;

namespace DocMine.UI.Tabs;

public sealed class InsertTab : TabPage, IBusyTab
{
    private readonly TextBox _csvBox;
    private readonly LinkLabel _useLastScanLink;
    private readonly CheckBox _hwpBox;
    private readonly CheckBox _pdfBox;
    private readonly Button _startBtn;
    private readonly Button _stopBtn;
    private readonly LogPane _log;
    private readonly ProgressEta _eta = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public InsertTab() : base("② 적재")
    {
        var info = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Gray,
            Text = "①스캔 결과 CSV 의 문서를 documents 테이블에 적재합니다.\n" +
                   "대상(HWP/PDF)을 선택하면 순차로 적재됩니다. 이미 적재된 파일은 자동 건너뜁니다.",
        };

        // 입력 CSV
        var csvGroup = new GroupBox { Text = "입력 CSV (스캔 결과)", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var csvRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _csvBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = Path.GetFullPath(AppConfig.DefaultScanCsv),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        var browseBtn = new Button { Text = "찾아보기…", AutoSize = true };
        browseBtn.Click += (_, _) => BrowseCsv();
        csvRow.Controls.Add(_csvBox, 0, 0);
        csvRow.Controls.Add(browseBtn, 1, 0);
        csvGroup.Controls.Add(csvRow);

        // 최근 스캔 결과 자동 채움 링크.
        _useLastScanLink = new LinkLabel
        {
            AutoSize = true, Visible = false,
            Padding = new Padding(2, 4, 0, 0), LinkColor = Color.SteelBlue,
        };
        _useLastScanLink.LinkClicked += (_, _) => UseLastScan();
        csvGroup.Controls.Add(_useLastScanLink);
        VisibleChanged += (_, _) => RefreshUseLastScanLink();

        // 대상 선택
        var tgtGroup = new GroupBox { Text = "적재 대상", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var tgtFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        _hwpBox = new CheckBox { Text = "HWP (.hwp/.hwpx — 한/글 COM)", Checked = true, AutoSize = true, Margin = new Padding(0, 0, 20, 0) };
        _pdfBox = new CheckBox { Text = "PDF (.pdf — PdfPig)", Checked = true, AutoSize = true };
        tgtFlow.Controls.Add(_hwpBox);
        tgtFlow.Controls.Add(_pdfBox);
        tgtGroup.Controls.Add(tgtFlow);

        // 버튼
        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
        _startBtn = new Button { Text = "적재 시작", AutoSize = true };
        _stopBtn  = new Button { Text = "중지",     AutoSize = true, Enabled = false, Margin = new Padding(6, 0, 0, 0) };
        _startBtn.Click += async (_, _) => await StartAsync();
        _stopBtn.Click  += (_, _) => OnStop();
        btnRow.Controls.Add(_startBtn);
        btnRow.Controls.Add(_stopBtn);

        // 로그
        _log = new LogPane { Dock = DockStyle.Fill };
        var logFrame = new GroupBox { Text = "로그", Dock = DockStyle.Fill, Padding = new Padding(4) };
        logFrame.Controls.Add(_log);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(info, 0, 0);
        root.Controls.Add(csvGroup, 0, 1);
        root.Controls.Add(tgtGroup, 0, 2);
        root.Controls.Add(btnRow, 0, 3);
        root.Controls.Add(logFrame, 0, 4);

        Controls.Add(root);
    }

    private void BrowseCsv()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "스캔 결과 CSV 선택",
            FileName = Path.GetFileName(_csvBox.Text),
            Filter = "CSV (*.csd;*.csv)|*.csd;*.csv|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _csvBox.Text = dlg.FileName;
    }

    private async Task StartAsync()
    {
        if (_busy) return;
        var csv = _csvBox.Text.Trim();
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

        _busy = true;
        _cts = new CancellationTokenSource();
        _startBtn.Enabled = false;
        _startBtn.Text = "적재 중…";
        _stopBtn.Enabled = true;
        _stopBtn.Text = "중지";
        _log.Clear();

        var cfg = AppConfig.Current;
        _log.AppendLine($"  settings: {cfg.SettingsPath}");
        _log.AppendLine($"  DB:       {cfg.DbUser}@{cfg.DbHost}:{cfg.DbPort}/{cfg.DbName} (table={cfg.DbTable})");
        _log.AppendLine($"  대상: {(_hwpBox.Checked ? "HWP " : "")}{(_pdfBox.Checked ? "PDF" : "")}".TrimEnd());

        var token = _cts.Token;
        try
        {
            if (_hwpBox.Checked)
            {
                _log.AppendLine("\n  ── HWP 적재 ──");
                _eta.Reset();
                var runner = new HwpInsertRunner(cfg);
                await Task.Run(() => runner.RunAsync(
                    csv, 0, null,
                    onLog: line => _log.AppendLine(line),
                    onProgress: OnHwpProgress,
                    cancellationToken: token), token);
            }
            if (_pdfBox.Checked)
            {
                _log.AppendLine("\n  ── PDF 적재 ──");
                _eta.Reset();
                var runner = new PdfInsertRunner(cfg);
                await Task.Run(() => runner.RunAsync(
                    csv, 0, null,
                    onLog: line => _log.AppendLine(line),
                    onProgress: OnPdfProgress,
                    cancellationToken: token), token);
            }
            _log.AppendLine("\n  전체 적재 완료.");
        }
        catch (OperationCanceledException)
        {
            _log.AppendLine("\n  중단됨.");
        }
        catch (Exception ex)
        {
            _log.AppendLine($"\n[오류] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
            _startBtn.Enabled = true;
            _startBtn.Text = "적재 시작";
            _stopBtn.Enabled = false;
            _stopBtn.Text = "중지";
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnHwpProgress(HwpInsertProgress p)
    {
        var pct = p.Total == 0 ? 100 : p.Index * 100.0 / p.Total;
        var crash = p.Crash > 0 ? $" crash:{p.Crash}" : "";
        var skip  = p.Skip  > 0 ? $" skip:{p.Skip}"   : "";
        var eta   = _eta.Format(p.Index, p.Total);
        _log.UpdateLive($"  [HWP {ProgressBar(pct, 30)}] {p.Index}/{p.Total}  ok:{p.Ok} err:{p.Err}{crash}{skip}{eta}");
    }

    private void OnPdfProgress(PdfInsertProgress p)
    {
        var pct = p.Total == 0 ? 100 : p.Index * 100.0 / p.Total;
        var empty = p.Empty > 0 ? $" empty:{p.Empty}" : "";
        var skip  = p.Skip  > 0 ? $" skip:{p.Skip}"   : "";
        var eta   = _eta.Format(p.Index, p.Total);
        _log.UpdateLive($"  [PDF {ProgressBar(pct, 30)}] {p.Index}/{p.Total}  ok:{p.Ok} err:{p.Err}{empty}{skip}{eta}");
    }

    private void OnStop()
    {
        if (!_busy) return;
        _cts?.Cancel();
        _stopBtn.Enabled = false;
        _stopBtn.Text = "중지 중…";
    }

    private static string ProgressBar(double pct, int width)
    {
        var n = (int)Math.Round(pct / 100.0 * width);
        n = Math.Clamp(n, 0, width);
        return new string('#', n) + new string('.', width - n);
    }

    // ─ 최근 스캔 결과 핸드오프 ─────────────────────────────────────
    private void RefreshUseLastScanLink()
    {
        if (!Visible) return;
        var last = ScanResultRegistry.LastScanCsv;
        if (string.IsNullOrEmpty(last)) { _useLastScanLink.Visible = false; return; }

        string current;
        try { current = Path.GetFullPath(_csvBox.Text.Trim()); }
        catch { current = ""; }
        if (string.Equals(last, current, StringComparison.OrdinalIgnoreCase))
        {
            _useLastScanLink.Visible = false;
            return;
        }
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
