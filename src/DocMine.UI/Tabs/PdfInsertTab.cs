// PdfInsertTab — Python unified_gui.PdfInsertTab 의 등가.
//
// CSV 경로 + start/end 범위 입력 → PdfInsertRunner 호출.
// 시작/중지 버튼은 CancellationTokenSource 로 워커 + INSERT 루프 함께 정리.
// 진행 통계는 라이브 라인으로 갱신 (LogPane.UpdateLive).

using DocMine.Core.Config;
using DocMine.Core.Pipeline;

namespace DocMine.UI.Tabs;

public sealed class PdfInsertTab : TabPage, IBusyTab
{
    private readonly TextBox _csvBox;
    private readonly LinkLabel _useLastScanLink;
    private readonly TextBox _startBox;
    private readonly TextBox _endBox;
    private readonly Button _startBtn;
    private readonly Button _stopBtn;
    private readonly LogPane _log;
    private readonly ProgressEta _eta = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public PdfInsertTab() : base("② PDF 적재")
    {
        var info = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.Gray,
            Text = "PdfPig 로 PDF 본문을 뽑아 documents 테이블에 적재합니다.\n" +
                   "스캔본/이미지 PDF 는 빈 본문이 나올 수 있습니다 (OCR 미적용 → 'empty' 분류).",
        };

        // CSV 경로
        var csvGroup = new GroupBox
        {
            Text = "입력 CSV (PDF 스캔 결과)",
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8),
        };
        var csvRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _csvBox = new TextBox
        {
            Dock = DockStyle.Fill,
            // 기본값은 코드 상수 (.csd) — settings 와 무관. 사용자가 다른 파일 선택 가능.
            Text = Path.GetFullPath(AppConfig.DefaultPdfCsv),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        var browseBtn = new Button { Text = "찾아보기…", AutoSize = true };
        browseBtn.Click += (_, _) => BrowseCsv();
        csvRow.Controls.Add(_csvBox, 0, 0);
        csvRow.Controls.Add(browseBtn, 1, 0);
        csvGroup.Controls.Add(csvRow);

        // 최근 스캔 결과 자동 채움 링크 — 탭 전환 시 갱신, 등록된 최근 경로가
        // 현재 입력란 값과 다를 때만 표시.
        _useLastScanLink = new LinkLabel
        {
            AutoSize = true,
            Visible = false,
            Padding = new Padding(2, 4, 0, 0),
            LinkColor = Color.SteelBlue,
        };
        _useLastScanLink.LinkClicked += (_, _) => UseLastScan();
        csvGroup.Controls.Add(_useLastScanLink);
        VisibleChanged += (_, _) => RefreshUseLastScanLink();

        // 범위
        var rngGroup = new GroupBox
        {
            Text = "처리 범위 (비우면 전체)",
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8),
        };
        var rngRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        rngRow.Controls.Add(new Label { Text = "start", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        _startBox = new TextBox { Text = "0", Width = 80 };
        rngRow.Controls.Add(_startBox);
        rngRow.Controls.Add(new Label { Text = "end", AutoSize = true, Padding = new Padding(12, 4, 4, 0) });
        _endBox = new TextBox { Width = 80 };
        rngRow.Controls.Add(_endBox);
        rngGroup.Controls.Add(rngRow);

        // 버튼
        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
        _startBtn = new Button { Text = "PDF 적재 시작", AutoSize = true };
        _stopBtn  = new Button { Text = "중지",          AutoSize = true, Enabled = false, Margin = new Padding(6, 0, 0, 0) };
        _startBtn.Click += async (_, _) => await StartAsync();
        _stopBtn.Click  += (_, _) => OnStop();
        btnRow.Controls.Add(_startBtn);
        btnRow.Controls.Add(_stopBtn);

        // 로그
        _log = new LogPane { Dock = DockStyle.Fill };
        var logFrame = new GroupBox { Text = "로그", Dock = DockStyle.Fill, Padding = new Padding(4) };
        logFrame.Controls.Add(_log);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(info, 0, 0);
        root.Controls.Add(csvGroup, 0, 1);
        root.Controls.Add(rngGroup, 0, 2);
        root.Controls.Add(btnRow, 0, 3);
        root.Controls.Add(logFrame, 0, 4);

        Controls.Add(root);
    }

    private void BrowseCsv()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "PDF CSV 선택",
            FileName = Path.GetFileName(_csvBox.Text),
            Filter = "CSV (*.csd)|*.csd|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _csvBox.Text = dlg.FileName;
    }

    private static int? ParseInt(string s, int? fallback)
    {
        s = s.Trim();
        if (s.Length == 0) return fallback;
        if (!int.TryParse(s, out var n)) throw new InvalidOperationException($"정수가 아닙니다: {s}");
        return n;
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
        int start; int? end;
        try
        {
            start = ParseInt(_startBox.Text, 0) ?? 0;
            end   = ParseInt(_endBox.Text, null);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "범위 오류");
            return;
        }

        _busy = true;
        _cts = new CancellationTokenSource();
        _startBtn.Enabled = false;
        _startBtn.Text = "PDF 적재 중…";
        _stopBtn.Enabled = true;
        _stopBtn.Text = "중지";
        _log.Clear();
        _eta.Reset();

        // 적용된 설정 경로 + DB 정보 명시.
        var cfg = AppConfig.Current;
        _log.AppendLine($"  settings: {cfg.SettingsPath}");
        _log.AppendLine($"  DB:       {cfg.DbUser}@{cfg.DbHost}:{cfg.DbPort}/{cfg.DbName} (table={cfg.DbTable})");

        try
        {
            var runner = new PdfInsertRunner(cfg);
            // RunAsync 를 background thread 에서 — UI thread 에서 직접 시작하면
            // 사전 점검 루프(skip 수만 건)가 await 없이 UI thread 를 독점하여
            // onProgress 의 BeginInvoke 가 UI 메시지 큐에 폭주 → 메인 GUI 죽음.
            var token = _cts.Token;
            await Task.Run(() => runner.RunAsync(
                csv, start, end,
                onLog: line => _log.AppendLine(line),
                onProgress: p =>
                {
                    var pct = p.Total == 0 ? 100 : p.Index * 100.0 / p.Total;
                    var bar = ProgressBar(pct, 30);
                    var empty = p.Empty > 0 ? $" empty:{p.Empty}" : "";
                    var skip  = p.Skip  > 0 ? $" skip:{p.Skip}"   : "";
                    var eta   = _eta.Format(p.Index, p.Total);
                    _log.UpdateLive($"  [{bar}] {p.Index}/{p.Total}  ok:{p.Ok} err:{p.Err}{empty}{skip}{eta}");
                },
                cancellationToken: token), token);
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
            _startBtn.Text = "PDF 적재 시작";
            _stopBtn.Enabled = false;
            _stopBtn.Text = "중지";
            _cts?.Dispose();
            _cts = null;
        }
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
        var last = ScanResultRegistry.PdfLast;
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
        var last = ScanResultRegistry.PdfLast;
        if (string.IsNullOrEmpty(last)) return;
        _csvBox.Text = last;
        _useLastScanLink.Visible = false;
    }

    // ─ IBusyTab ─────────────────────────────────────────────────────
    public bool IsBusy => _busy;
    public void RequestStop() => OnStop();
}
