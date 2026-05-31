// HwpInsertTab — Python unified_gui.InsertTab 의 등가.
//
// PdfInsertTab 과 구조 동일. 차이점:
//   - HwpInsertRunner 호출 (HwpWorker.exe spawn)
//   - 기본 CSV = AppConfig.DefaultHwpCsv (.csd)
//   - 라벨/메시지 "HWP" 로 변경
//   - "공유 한/글 보호" 옵션 (한/글 외부에 띄워둔 상태에서 워커가 같이 죽이지 않도록)

using DocMine.Core.Config;
using DocMine.Core.Pipeline;

namespace DocMine.UI.Tabs;

public sealed class HwpInsertTab : TabPage, IBusyTab
{
    private readonly TextBox _csvBox;
    private readonly TextBox _startBox;
    private readonly TextBox _endBox;
    private readonly CheckBox _keepHwpBox;
    private readonly Button _startBtn;
    private readonly Button _stopBtn;
    private readonly LogPane _log;
    private readonly ProgressEta _eta = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public HwpInsertTab() : base("② HWP 적재")
    {
        var info = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.Gray,
            Text = "HWP/HWPX 본문을 한/글 COM 으로 파싱해 documents 테이블에 적재합니다.\n" +
                   "워커 프로세스 격리 — COM 크래시 시 자동 재시작.",
        };

        // CSV
        var csvGroup = new GroupBox { Text = "입력 CSV (HWP 스캔 결과)", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var csvRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _csvBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = Path.GetFullPath(AppConfig.DefaultHwpCsv),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        var browseBtn = new Button { Text = "찾아보기…", AutoSize = true };
        browseBtn.Click += (_, _) => BrowseCsv();
        csvRow.Controls.Add(_csvBox, 0, 0);
        csvRow.Controls.Add(browseBtn, 1, 0);
        csvGroup.Controls.Add(csvRow);

        // 범위
        var rngGroup = new GroupBox { Text = "처리 범위 (비우면 전체)", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var rngRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        rngRow.Controls.Add(new Label { Text = "start", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        _startBox = new TextBox { Text = "0", Width = 80 };
        rngRow.Controls.Add(_startBox);
        rngRow.Controls.Add(new Label { Text = "end", AutoSize = true, Padding = new Padding(12, 4, 4, 0) });
        _endBox = new TextBox { Width = 80 };
        rngRow.Controls.Add(_endBox);
        rngGroup.Controls.Add(rngRow);

        // 옵션 — 외부 한/글 보호.
        var optGroup = new GroupBox { Text = "옵션", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        _keepHwpBox = new CheckBox
        {
            Text = "외부에 떠 있는 한/글(Hwp.exe) 죽이지 않기 (작업 중인 한/글 보호)",
            AutoSize = true, Checked = false,
        };
        optGroup.Controls.Add(_keepHwpBox);

        // 버튼
        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
        _startBtn = new Button { Text = "HWP 적재 시작", AutoSize = true };
        _stopBtn  = new Button { Text = "중지",        AutoSize = true, Enabled = false, Margin = new Padding(6, 0, 0, 0) };
        _startBtn.Click += async (_, _) => await StartAsync();
        _stopBtn.Click  += (_, _) => OnStop();
        btnRow.Controls.Add(_startBtn);
        btnRow.Controls.Add(_stopBtn);

        _log = new LogPane { Dock = DockStyle.Fill };
        var logFrame = new GroupBox { Text = "로그", Dock = DockStyle.Fill, Padding = new Padding(4) };
        logFrame.Controls.Add(_log);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(info, 0, 0);
        root.Controls.Add(csvGroup, 0, 1);
        root.Controls.Add(rngGroup, 0, 2);
        root.Controls.Add(optGroup, 0, 3);
        root.Controls.Add(btnRow, 0, 4);
        root.Controls.Add(logFrame, 0, 5);

        Controls.Add(root);
    }

    private void BrowseCsv()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "HWP CSV 선택",
            FileName = Path.GetFileName(_csvBox.Text),
            Filter = "CSV (*.csd)|*.csd|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _csvBox.Text = dlg.FileName;
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
        _startBtn.Text = "HWP 적재 중…";
        _stopBtn.Enabled = true;
        _stopBtn.Text = "중지";
        _log.Clear();
        _eta.Reset();
        _log.EnableMirror("hwp_insert.log");

        var cfg = AppConfig.Current;
        _log.AppendLine($"  settings: {cfg.SettingsPath}");
        _log.AppendLine($"  DB:       {cfg.DbUser}@{cfg.DbHost}:{cfg.DbPort}/{cfg.DbName} (table={cfg.DbTable})");
        _log.AppendLine($"  외부 한/글 보호: {(_keepHwpBox.Checked ? "ON" : "OFF")}");

        try
        {
            var runner = new HwpInsertRunner(cfg, keepHwp: _keepHwpBox.Checked);
            // background thread — UI thread 독점 + onProgress BeginInvoke 폭주 회피.
            var token = _cts.Token;
            await Task.Run(() => runner.RunAsync(
                csv, start, end,
                onLog: line => _log.AppendLine(line),
                onProgress: p =>
                {
                    var pct = p.Total == 0 ? 100 : p.Index * 100.0 / p.Total;
                    var bar = ProgressBar(pct, 30);
                    var crash = p.Crash > 0 ? $" crash:{p.Crash}" : "";
                    var skip  = p.Skip  > 0 ? $" skip:{p.Skip}"   : "";
                    var eta   = _eta.Format(p.Index, p.Total);
                    _log.UpdateLive($"  [{bar}] {p.Index}/{p.Total}  ok:{p.Ok} err:{p.Err}{crash}{skip}{eta}");
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
            _startBtn.Text = "HWP 적재 시작";
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

    // ─ IBusyTab ─────────────────────────────────────────────────────
    public bool IsBusy => _busy;
    public void RequestStop() => OnStop();
}
