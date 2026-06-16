// ExtractorTab — Python extractor_gui.ExtractorApp 의 등가.
//
// 폴더(재귀) 또는 단일 파일을 입력으로 받아, 선택한 포맷(HWP/HWPX, PDF) 의 파일을
// 모두 하나의 TXT 로 합쳐 저장.
//
// HWP/HWPX 는 HwpWorker.exe 를 --keep-hwp 모드로 띄워 사용 (사용자가 외부에
// 띄워둔 한/글이 추출 중 같이 종료되지 않도록).
// PDF 는 PdfPig 직접 호출 (in-process).

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DocMine.Core.Hwp;
using DocMine.Core.Pdf;

namespace DocMine.UI.Tabs;

public sealed class ExtractorTab : TabPage, IBusyTab
{
    private static readonly HashSet<string> HwpExts = new(StringComparer.OrdinalIgnoreCase) { ".hwp", ".hwpx" };
    private static readonly HashSet<string> PdfExts = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };
    private const string Separator = "================================================================================";

    private readonly RadioButton _modeFolder, _modeFile;
    private readonly CheckBox _hwpBox, _pdfBox;
    private readonly TextBox _srcBox, _dstBox;
    private readonly Button _srcBrowseBtn, _dstBrowseBtn;
    private readonly Button _startBtn, _stopBtn, _openOutBtn;
    private readonly ProgressBar _progBar;
    private readonly LogPane _log;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private string? _lastOutputPath;

    // 사용자가 dst 를 직접 편집/선택했는지 — false 일 때만 src/mode 변경 시 dst 자동 갱신.
    private bool _dstIsAuto = true;
    private bool _suppressDstTrace;

    public ExtractorTab() : base("④ 문서 추출")
    {
        // 모드
        var modeGroup = new GroupBox { Text = "변환 대상", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var modeFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        _modeFolder = new RadioButton { Text = "폴더 전체 (하위 포함)", Checked = true, AutoSize = true };
        _modeFile   = new RadioButton { Text = "파일 1개", AutoSize = true, Margin = new Padding(20, 0, 0, 0) };
        _modeFolder.CheckedChanged += (_, _) => { if (_modeFolder.Checked) UpdateAutoDst(); };
        _modeFile.CheckedChanged   += (_, _) => { if (_modeFile.Checked)   UpdateAutoDst(); };
        modeFlow.Controls.Add(_modeFolder);
        modeFlow.Controls.Add(_modeFile);
        modeGroup.Controls.Add(modeFlow);

        // 포맷
        var fmtGroup = new GroupBox { Text = "포맷 선택", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var fmtFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        _hwpBox = new CheckBox { Text = "HWP / HWPX (한/글 COM 파싱)", Checked = true,  AutoSize = true };
        _pdfBox = new CheckBox { Text = "PDF (PdfPig 텍스트 추출)",      Checked = false, AutoSize = true, Margin = new Padding(20, 0, 0, 0) };
        fmtFlow.Controls.Add(_hwpBox);
        fmtFlow.Controls.Add(_pdfBox);
        fmtGroup.Controls.Add(fmtFlow);

        // 입력 경로
        var srcGroup = new GroupBox { Text = "입력 경로", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var srcRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        srcRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        srcRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _srcBox = new TextBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _srcBox.TextChanged += (_, _) => UpdateAutoDst();
        _srcBrowseBtn = new Button { Text = "찾아보기…", AutoSize = true };
        _srcBrowseBtn.Click += (_, _) => BrowseSrc();
        srcRow.Controls.Add(_srcBox, 0, 0);
        srcRow.Controls.Add(_srcBrowseBtn, 1, 0);
        srcGroup.Controls.Add(srcRow);

        // 출력 TXT
        var dstGroup = new GroupBox { Text = "저장 파일 (TXT)", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var dstRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        dstRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dstRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _dstBox = new TextBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _dstBox.TextChanged += (_, _) => { if (!_suppressDstTrace) _dstIsAuto = false; };
        _dstBrowseBtn = new Button { Text = "저장 위치…", AutoSize = true };
        _dstBrowseBtn.Click += (_, _) => BrowseDst();
        dstRow.Controls.Add(_dstBox, 0, 0);
        dstRow.Controls.Add(_dstBrowseBtn, 1, 0);
        dstGroup.Controls.Add(dstRow);

        // 버튼
        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
        _startBtn   = new Button { Text = "추출 시작",  AutoSize = true };
        _stopBtn    = new Button { Text = "중지",       AutoSize = true, Enabled = false, Margin = new Padding(6, 0, 0, 0) };
        _openOutBtn = new Button { Text = "산출물 열기", AutoSize = true, Enabled = false, Margin = new Padding(6, 0, 0, 0) };
        _startBtn.Click   += async (_, _) => await StartAsync();
        _stopBtn.Click    += (_, _) => OnStop();
        _openOutBtn.Click += (_, _) => OpenLastOutput();
        btnRow.Controls.Add(_startBtn);
        btnRow.Controls.Add(_stopBtn);
        btnRow.Controls.Add(_openOutBtn);

        // 진행률 막대
        _progBar = new ProgressBar { Dock = DockStyle.Top, Height = 14, Margin = new Padding(0, 4, 0, 4), Style = ProgressBarStyle.Continuous };

        _log = new LogPane { Dock = DockStyle.Fill };
        var logFrame = new GroupBox { Text = "로그", Dock = DockStyle.Fill, Padding = new Padding(4) };
        logFrame.Controls.Add(_log);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(modeGroup, 0, 0);
        root.Controls.Add(fmtGroup, 0, 1);
        root.Controls.Add(srcGroup, 0, 2);
        root.Controls.Add(dstGroup, 0, 3);
        root.Controls.Add(btnRow, 0, 4);
        root.Controls.Add(_progBar, 0, 5);
        root.Controls.Add(logFrame, 0, 6);

        Controls.Add(root);
    }

    private void BrowseSrc()
    {
        if (_modeFolder.Checked)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "변환할 폴더 선택 (하위 포함)",
                ShowNewFolderButton = false,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _srcBox.Text = dlg.SelectedPath;
        }
        else
        {
            using var dlg = new OpenFileDialog
            {
                Title = "변환할 파일 선택",
                Filter = "문서 (*.hwp;*.hwpx;*.pdf)|*.hwp;*.hwpx;*.pdf|모든 파일 (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _srcBox.Text = dlg.FileName;
        }
    }

    private void BrowseDst()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "TXT 저장 위치",
            FileName = Path.GetFileName(_dstBox.Text),
            DefaultExt = "txt",
            Filter = "텍스트 (*.txt)|*.txt|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _dstBox.Text = dlg.FileName;
    }

    private void UpdateAutoDst()
    {
        if (!_dstIsAuto) return;
        var src = _srcBox.Text.Trim();
        if (src.Length == 0) return;
        string dst;
        try
        {
            if (_modeFile.Checked)
                dst = Path.ChangeExtension(src, ".txt");
            else
            {
                // 폴더 추출 — 결과 TXT 를 그 폴더 '안' 에 둔다. 기존엔 GetDirectoryName 이
                // 폴더의 *상위* 를 반환해 한 단계 위에 저장되던 버그 (+ trailing '\' 유무로
                // 동작이 갈렸음). trailing 슬래시를 정규화해 폴더명만 안전하게 뽑는다.
                var name = Path.GetFileName(src.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) name = "extracted";  // 드라이브 루트 등
                dst = Path.Combine(src, name + "_extracted.txt");
            }
        }
        catch { return; }

        _suppressDstTrace = true;
        _dstBox.Text = dst;
        _suppressDstTrace = false;
    }

    private async Task StartAsync()
    {
        if (_busy) return;

        var src = _srcBox.Text.Trim();
        var dst = _dstBox.Text.Trim();
        if (src.Length == 0 || dst.Length == 0)
        {
            MessageBox.Show(this, "입력 경로와 출력 파일을 모두 지정하세요.", "경로 누락");
            return;
        }
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_hwpBox.Checked) exts.UnionWith(HwpExts);
        if (_pdfBox.Checked) exts.UnionWith(PdfExts);
        if (exts.Count == 0)
        {
            MessageBox.Show(this, "포맷을 하나 이상 선택하세요.", "포맷 선택");
            return;
        }

        // 입력 파일 수집.
        List<string> files;
        try
        {
            files = CollectFiles(src, _modeFile.Checked, exts);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "입력 경로 오류");
            return;
        }
        if (files.Count == 0)
        {
            MessageBox.Show(this, "선택한 포맷의 파일이 없습니다.", "파일 없음");
            return;
        }

        _busy = true;
        _cts = new CancellationTokenSource();
        _startBtn.Enabled = false;
        _startBtn.Text = "추출 중…";
        _stopBtn.Enabled = true;
        _stopBtn.Text = "중지";
        _openOutBtn.Enabled = false;
        _progBar.Value = 0;
        _progBar.Maximum = files.Count;
        _lastOutputPath = null;
        _log.Clear();
        _log.AppendLine($"  입력: {files.Count}건");
        _log.AppendLine($"  출력: {dst}");

        try
        {
            await Task.Run(() => Extract(files, dst, _cts.Token), _cts.Token);
            _log.AppendLine($"\n  ✓ 완료 — {dst}");
            _lastOutputPath = dst;
            _openOutBtn.Enabled = File.Exists(dst);
        }
        catch (OperationCanceledException)
        {
            _log.AppendLine("\n  중단됨.");
            // 중단됐어도 일부 결과는 저장됐을 수 있으므로 산출물 열기 enable.
            _lastOutputPath = dst;
            _openOutBtn.Enabled = File.Exists(dst);
        }
        catch (Exception ex)
        {
            _log.AppendLine($"\n[오류] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
            _startBtn.Enabled = true;
            _startBtn.Text = "추출 시작";
            _stopBtn.Enabled = false;
            _stopBtn.Text = "중지";
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OpenLastOutput()
    {
        if (string.IsNullOrEmpty(_lastOutputPath) || !File.Exists(_lastOutputPath)) return;
        try { Process.Start(new ProcessStartInfo(_lastOutputPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "열기 실패"); }
    }

    private void OnStop()
    {
        if (!_busy) return;
        _cts?.Cancel();
        _stopBtn.Enabled = false;
        _stopBtn.Text = "중지 중…";
    }

    private static List<string> CollectFiles(string src, bool singleFile, IReadOnlySet<string> exts)
    {
        if (singleFile)
        {
            if (!File.Exists(src)) throw new FileNotFoundException(src);
            return exts.Contains(Path.GetExtension(src))
                ? new List<string> { src }
                : new List<string>();
        }
        if (!Directory.Exists(src)) throw new DirectoryNotFoundException(src);
        return Directory.EnumerateFiles(src, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        })
        .Where(f => exts.Contains(Path.GetExtension(f)))
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToList();
    }

    private void Extract(List<string> files, string dstPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dstPath))!);

        // HWP 워커는 필요할 때만 spawn. PDF 만 있으면 워커 없이.
        Process? hwpWorker = null;
        Task? stderrTask = null;
        try
        {
            if (files.Any(f => HwpExts.Contains(Path.GetExtension(f))))
            {
                hwpWorker = StartHwpWorker(out stderrTask);
                _log.AppendLine($"  ✓ HwpWorker 시작 (PID {hwpWorker.Id})");
            }

            using var w = new StreamWriter(dstPath, append: false, new UTF8Encoding(true));
            var sw = Stopwatch.StartNew();
            var done = 0;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                _log.UpdateLive($"  [{done + 1}/{files.Count}] {Path.GetFileName(f)}");

                string text;
                try
                {
                    text = HwpExts.Contains(Path.GetExtension(f))
                        ? ExtractHwp(hwpWorker!, f, ct)
                        : PdfTextExtractor.Extract(f);
                }
                catch (Exception ex)
                {
                    _log.AppendLine($"\n  [오류] {Path.GetFileName(f)}: {ex.Message}");
                    done++; continue;
                }

                w.WriteLine(Separator);
                w.WriteLine($"파일: {f}");
                w.WriteLine(Separator);
                w.WriteLine(text);
                w.WriteLine();
                w.Flush();
                done++;
                UpdateProgress(done);
            }
            _log.AppendLine($"\n  처리 {done}/{files.Count}건  ({sw.Elapsed.TotalSeconds:F1}초)");
        }
        finally
        {
            if (hwpWorker is not null)
            {
                try { hwpWorker.StandardInput.WriteLine("{\"op\":\"quit\"}"); hwpWorker.StandardInput.Flush(); } catch { }
                try { hwpWorker.WaitForExit(3000); } catch { }
                try { if (!hwpWorker.HasExited) hwpWorker.Kill(entireProcessTree: true); } catch { }
                hwpWorker.Dispose();
            }
        }
    }

    private static Process StartHwpWorker(out Task? stderrTask)
    {
        // 자기 자신(python.exe) 을 워커 모드로 재실행.
        var workerPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath null — 워커 spawn 불가");
        var psi = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--hwp-worker");
        // 추출기에서는 외부 한/글 보호.
        psi.ArgumentList.Add("--keep-hwp");

        var p = Process.Start(psi) ?? throw new InvalidOperationException("HwpWorker spawn 실패");
        var captured = p;
        stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await captured.StandardError.ReadLineAsync()) is not null) { /* swallow */ }
            }
            catch { }
        });
        return p;
    }

    private static string ExtractHwp(Process worker, string path, CancellationToken ct)
    {
        var req = new { op = "parse", idx = 0, path = Path.GetFullPath(path), ext = Path.GetExtension(path).ToLowerInvariant() };
        var reqJson = JsonSerializer.Serialize(req, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        worker.StandardInput.WriteLine(reqJson);
        worker.StandardInput.Flush();

        var line = worker.StandardOutput.ReadLine() ?? throw new IOException("worker stdout EOF");
        var resp = JsonSerializer.Deserialize<Resp>(line, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        })!;
        if (resp.Status == "error") throw new InvalidOperationException(resp.Err ?? "(unknown)");
        return resp.Text ?? "";
    }

    private sealed record Resp(int Idx, string Status, string? Text, string? Err);

    private void UpdateProgress(int done)
    {
        if (_progBar.InvokeRequired) { _progBar.BeginInvoke(() => UpdateProgress(done)); return; }
        _progBar.Value = Math.Min(done, _progBar.Maximum);
    }

    // ─ IBusyTab ─────────────────────────────────────────────────────
    public bool IsBusy => _busy;
    public void RequestStop() => OnStop();
}
