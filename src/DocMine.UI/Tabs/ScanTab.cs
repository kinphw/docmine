// ScanTab — HWP / PDF 공용 스캔 탭. Python unified_gui.ScanTab 의 'scope' 분기 등가.
//
// scope='hwp'  → .hwp/.hwpx, 출력 CSV = config.CsvFile
// scope='pdf'  → .pdf,        출력 CSV = config.PdfCsvFile

using System.Drawing;
using DocMine.Core.Config;
using DocMine.Core.Scanning;
using DocMine.Win32;

namespace DocMine.UI.Tabs;

public sealed class ScanTab : TabPage, IBusyTab
{
    public enum Scope { Hwp, Pdf }

    private readonly Scope _scope;
    private readonly LogPane _log;
    private readonly TextBox _outBox;
    private readonly List<(CheckBox Box, string Path)> _driveBoxes = new();
    private readonly List<(CheckBox Box, string Ext)> _extBoxes = new();
    private readonly Button _startBtn;
    private CancellationTokenSource? _cts;

    public ScanTab(Scope scope) : base(scope == Scope.Hwp ? "① HWP 스캔" : "① PDF 스캔")
    {
        _scope = scope;
        var label = scope == Scope.Hwp ? "HWP" : "PDF";

        var defaultExts = scope == Scope.Hwp
            ? new[] { ".hwp", ".hwpx" }
            : new[] { ".pdf" };
        // 출력 CSV 기본값은 AppConfig 상수 (.csd) — settings.json 에 저장 안 함.
        // 사용자가 "저장 위치…" 또는 텍스트 박스 직접 편집으로 매 실행마다 변경 가능.
        var defaultCsv = scope == Scope.Hwp ? AppConfig.DefaultHwpCsv : AppConfig.DefaultPdfCsv;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        // ── 확장자 그룹 ──────────────────────────────────────────────
        var extGroup = new GroupBox
        {
            Text = $"{label} 대상 확장자",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
        };
        var extFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        foreach (var ext in defaultExts)
        {
            var cb = new CheckBox { Text = ext, Checked = true, AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
            _extBoxes.Add((cb, ext));
            extFlow.Controls.Add(cb);
        }
        extGroup.Controls.Add(extFlow);
        root.Controls.Add(extGroup, 0, 0);

        // ── 드라이브 그룹 ────────────────────────────────────────────
        var driveGroup = new GroupBox
        {
            Text = "스캔 대상 드라이브",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
        };
        var driveFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        // 기본 체크는 고정 디스크(내장 HDD/SSD) 만 — 사용자가 가장 흔히 원하는 동작.
        // 네트워크/이동식/CD-ROM 은 unchecked 로 표시되며 사용자가 명시 체크.
        IReadOnlyList<DocMine.Win32.DriveInfo> drives;
        try { drives = Drives.List(); }
        catch (Exception ex)
        {
            drives = Array.Empty<DocMine.Win32.DriveInfo>();
            driveFlow.Controls.Add(new Label { Text = $"(드라이브 열거 실패: {ex.Message})", ForeColor = Color.Red, AutoSize = true });
        }
        if (drives.Count == 0)
        {
            driveFlow.Controls.Add(new Label { Text = "(마운트된 드라이브가 없습니다)", ForeColor = Color.Gray, AutoSize = true });
        }
        foreach (var d in drives)
        {
            var cb = new CheckBox
            {
                Text = $"{d.Root}    {(string.IsNullOrEmpty(d.Label) ? "(라벨 없음)" : d.Label)}    [{d.DriveTypeName}]",
                Checked = d.DriveTypeName == "고정",
                AutoSize = true,
            };
            _driveBoxes.Add((cb, d.Root));
            driveFlow.Controls.Add(cb);
        }

        var quickFlow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var allOn  = new Button { Text = "전체 선택", AutoSize = true };
        var allOff = new Button { Text = "전체 해제", AutoSize = true };
        allOn.Click  += (_, _) => SetAllDrives(true);
        allOff.Click += (_, _) => SetAllDrives(false);
        quickFlow.Controls.Add(allOn);
        quickFlow.Controls.Add(allOff);
        driveFlow.Controls.Add(quickFlow);
        driveGroup.Controls.Add(driveFlow);
        root.Controls.Add(driveGroup, 0, 1);

        // ── 출력 CSV ────────────────────────────────────────────────
        var outGroup = new GroupBox
        {
            Text = $"{label} 출력 CSV",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
        };
        var outRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _outBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = Path.GetFullPath(defaultCsv),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        var browseBtn = new Button { Text = "저장 위치…", AutoSize = true };
        browseBtn.Click += (_, _) => BrowseOut();
        outRow.Controls.Add(_outBox, 0, 0);
        outRow.Controls.Add(browseBtn, 1, 0);
        outGroup.Controls.Add(outRow);
        root.Controls.Add(outGroup, 0, 2);

        // ── 실행 버튼 ────────────────────────────────────────────────
        var btnFlow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
        _startBtn = new Button { Text = $"{label} 스캔 시작", AutoSize = true };
        _startBtn.Click += async (_, _) => await OnStartAsync();
        btnFlow.Controls.Add(_startBtn);
        root.Controls.Add(btnFlow, 0, 3);

        // ── 로그 ────────────────────────────────────────────────────
        _log = new LogPane { Dock = DockStyle.Fill };
        var logGroup = new GroupBox { Text = "로그", Dock = DockStyle.Fill, Padding = new Padding(4) };
        logGroup.Controls.Add(_log);
        root.Controls.Add(logGroup, 0, 4);

        Controls.Add(root);
    }

    private void SetAllDrives(bool value)
    {
        foreach (var (cb, _) in _driveBoxes) cb.Checked = value;
    }

    private void BrowseOut()
    {
        using var dlg = new SaveFileDialog
        {
            Title    = $"{(_scope == Scope.Hwp ? "HWP" : "PDF")} 스캔 결과 CSV 저장 위치",
            FileName = Path.GetFileName(_outBox.Text),
            DefaultExt = "csd",
            Filter   = "CSV (*.csd)|*.csd|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _outBox.Text = dlg.FileName;
    }

    private async Task OnStartAsync()
    {
        var label = _scope == Scope.Hwp ? "HWP" : "PDF";
        var drives = _driveBoxes.Where(p => p.Box.Checked).Select(p => p.Path).ToList();
        if (drives.Count == 0)
        {
            MessageBox.Show(this, "스캔할 드라이브를 하나 이상 선택하세요.", "드라이브 선택");
            return;
        }
        var exts = _extBoxes.Where(p => p.Box.Checked).Select(p => p.Ext).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (exts.Count == 0)
        {
            MessageBox.Show(this, "대상 확장자를 하나 이상 선택하세요.", "확장자 선택");
            return;
        }
        var outPath = _outBox.Text.Trim();
        if (string.IsNullOrEmpty(outPath))
        {
            MessageBox.Show(this, "출력 CSV 경로를 지정하세요.", "출력 경로");
            return;
        }

        _startBtn.Enabled = false;
        _startBtn.Text = $"{label} 스캔 중…";
        _log.Clear();
        _log.AppendLine("=" + new string('=', 60));
        _log.AppendLine($"  Step 1 — {label} 파일 스캐너");
        _log.AppendLine($"  대상 드라이브: {string.Join(", ", drives)}");
        _log.AppendLine($"  대상 확장자  : {string.Join(", ", exts.OrderBy(s => s))}");
        _log.AppendLine($"  출력 파일    : {Path.GetFullPath(outPath)}");
        _log.AppendLine("=" + new string('=', 60));

        _cts = new CancellationTokenSource();
        try
        {
            var cfg = AppConfig.Current;
            if (cfg.ScanExcludeDirs.Count > 0)
            {
                _log.AppendLine($"  예외 폴더 {cfg.ScanExcludeDirs.Count}개:");
                foreach (var ex in cfg.ScanExcludeDirs) _log.AppendLine($"    - {ex}");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await Task.Run(() => DriveScanner.Scan(
                drives, exts,
                onProgress:    n => _log.AppendLine($"    ... {n:N0}개 발견"),
                onRootStart:   r => _log.AppendLine($"\n  [{r}] 스캔 시작..."),
                // 라이브 라인 — milestone 메시지가 들어오면 AppendLine 이 확정시키므로
                // 첫 1000건까지 침묵하던 자리에 2초마다 "현재: <폴더>" 가 갱신됨.
                onCurrentDir:  d => _log.UpdateLive($"    현재: {d}"),
                excludeDirs:   cfg.ScanExcludeDirs,
                cancellationToken: _cts.Token));

            if (result.Files.Count == 0)
            {
                _log.AppendLine("\n  결과 없음 — 지정 확장자 파일을 찾지 못했습니다.");
            }
            else
            {
                DriveScanner.WriteCsv(result.Files, outPath);
                _log.AppendLine("\n" + new string('=', 60));
                _log.AppendLine($"  완료: 총 {result.Files.Count:N0}개 파일  ({sw.Elapsed.TotalSeconds:F1}초)");
                var byExt = result.Files.GroupBy(f => f.Extension).OrderBy(g => g.Key);
                foreach (var g in byExt)
                    _log.AppendLine($"    {g.Key,-8}: {g.Count():N0}개");
                _log.AppendLine($"  저장: {Path.GetFullPath(outPath)}");
                if (result.InaccessibleCount > 0)
                    _log.AppendLine($"  ⚠ 접근 불가 {result.InaccessibleCount}건 (권한 — 무시됨)");
                _log.AppendLine(new string('=', 60));
            }
        }
        catch (OperationCanceledException)
        {
            _log.AppendLine("\n  중지됨.");
        }
        catch (Exception ex)
        {
            _log.AppendLine($"\n[오류] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _startBtn.Enabled = true;
            _startBtn.Text = $"{label} 스캔 시작";
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ─ IBusyTab ─────────────────────────────────────────────────────
    public bool IsBusy => _cts is not null;
    public void RequestStop() => _cts?.Cancel();
}
