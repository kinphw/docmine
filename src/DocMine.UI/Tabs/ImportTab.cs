// ImportTab — 반입. 반출(본문 포함 전송 CSV)의 역연산.
//
// 환경1 '반출'(선택 항목 → body_text 포함 CSV)을 환경2로 옮겨와, 파일 재파싱 없이
// DB 에 그대로 적재한다. (directory,filename) UNIQUE 키 기준 upsert 라 멱등 —
// 같은 CSV 를 두 번 반입해도 중복 행이 생기지 않고 갱신만 된다.
//
// 적재(②)와의 차이: 적재는 스캔 CSV + 실제 파일 재파싱(파일 필요, DRM 재파싱 위험),
// 반입은 이미 파싱된 레코드를 통째 이전(파일 불필요, 재파싱 없음).

using DocMine.Core.Config;
using DocMine.Core.Db;
using DocMine.Core.Pipeline;

namespace DocMine.UI.Tabs;

public sealed class ImportTab : TabPage, IBusyTab
{
    private DocumentRepository _repo;
    private readonly TextBox _csvBox;
    private readonly Button _verifyBtn, _importBtn, _stopBtn;
    private readonly Label _statusLabel;
    private readonly LogPane _log;
    private readonly ProgressEta _eta = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public ImportTab() : base("반입")
    {
        _repo = new DocumentRepository(AppConfig.Current);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // ── 상단 ────────────────────────────────────────────────────────
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 3 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var info = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Gray,
            Text = "다른 환경의 '반출'(본문 포함 CSV)을 현재 DB 에 적재합니다. 파일 재파싱 없이 레코드를 그대로 이전합니다.\n" +
                   "(폴더+파일명) 기준 upsert — 이미 있는 행은 갱신, 없는 행은 신규 삽입. 같은 CSV 재반입해도 안전.",
        };
        top.Controls.Add(info, 0, 0);

        var csvRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        csvRow.Controls.Add(new Label { Text = "반입 CSV:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _csvBox = new TextBox { Width = 380 };
        csvRow.Controls.Add(_csvBox);
        var browseBtn = new Button { Text = "찾아보기…", AutoSize = true, Margin = new Padding(4, 2, 0, 0) };
        browseBtn.Click += (_, _) => BrowseCsv();
        csvRow.Controls.Add(browseBtn);
        top.Controls.Add(csvRow, 0, 1);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        _verifyBtn = new Button { Text = "검증 (내용 확인)", AutoSize = true };
        _verifyBtn.Click += async (_, _) => await VerifyAsync();
        bar.Controls.Add(_verifyBtn);
        _importBtn = new Button { Text = "반입", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        _importBtn.Click += async (_, _) => await ImportAsync();
        bar.Controls.Add(_importBtn);
        _stopBtn = new Button { Text = "중지", AutoSize = true, Enabled = false, Margin = new Padding(8, 0, 0, 0) };
        _stopBtn.Click += (_, _) => OnStop();
        bar.Controls.Add(_stopBtn);
        _statusLabel = new Label { Text = "", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(12, 6, 0, 0) };
        bar.Controls.Add(_statusLabel);
        top.Controls.Add(bar, 0, 2);

        root.Controls.Add(top, 0, 0);

        // ── 로그 ──────────────────────────────────────────────────────────
        _log = new LogPane { Dock = DockStyle.Fill };
        var logFrame = new GroupBox { Text = "로그", Dock = DockStyle.Fill, Padding = new Padding(4) };
        logFrame.Controls.Add(_log);
        root.Controls.Add(logFrame, 0, 1);

        Controls.Add(root);

        VisibleChanged += (_, _) => { if (Visible) _repo = new DocumentRepository(AppConfig.Current); };
    }

    private void BrowseCsv()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "반입할 반출 CSV 선택 (본문 포함)",
            FileName = Path.GetFileName(_csvBox.Text),
            Filter = "CSV (*.csv;*.csd)|*.csv;*.csd|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _csvBox.Text = dlg.FileName;
    }

    private string? ValidatedCsv()
    {
        var csv = _csvBox.Text.Trim();
        if (string.IsNullOrEmpty(csv) || !File.Exists(csv))
        {
            MessageBox.Show(this, $"반입할 CSV 파일을 찾을 수 없습니다:\n{csv}", "CSV 없음");
            return null;
        }
        return csv;
    }

    private async Task VerifyAsync()
    {
        if (_busy) return;
        var csv = ValidatedCsv();
        if (csv is null) return;

        _verifyBtn.Enabled = false;
        _statusLabel.Text = "검증 중…";
        try
        {
            var records = await Task.Run(() => DocTransferCsv.Read(csv));
            var withBody = records.Count(r => !string.IsNullOrEmpty(r.BodyText));
            var byExt = records.GroupBy(r => string.IsNullOrEmpty(r.Extension) ? "(없음)" : r.Extension.ToLowerInvariant())
                               .OrderByDescending(g => g.Count())
                               .Select(g => $"{g.Key} {g.Count():N0}");
            _statusLabel.Text = $"{records.Count:N0}건 · 본문 {withBody:N0}건";
            _log.AppendLine($"[검증] {Path.GetFileName(csv)} → {records.Count:N0}건 (본문 {withBody:N0} / 본문없음 {records.Count - withBody:N0})");
            _log.AppendLine($"  확장자: {string.Join(" · ", byExt)}");
            if (withBody == 0 && records.Count > 0)
                _log.AppendLine("  ⚠ 본문이 하나도 없습니다 — 메타데이터만 든 CSV(manifest?)일 수 있습니다.");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "오류";
            MessageBox.Show(this, ex.Message, "검증 오류");
            _log.AppendLine($"[오류] {ex.Message}");
        }
        finally
        {
            _verifyBtn.Enabled = true;
        }
    }

    private async Task ImportAsync()
    {
        if (_busy) return;
        var csv = ValidatedCsv();
        if (csv is null) return;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        _log.Clear();
        var cfg = AppConfig.Current;
        _log.AppendLine($"  DB: {cfg.DbUser}@{cfg.DbHost}:{cfg.DbPort}/{cfg.DbName} (table={cfg.DbTable})");
        _log.AppendLine($"  반입 CSV: {csv}");

        var token = _cts.Token;
        try
        {
            var result = await Task.Run(() =>
            {
                var records = DocTransferCsv.Read(csv);
                _log.AppendLine($"  읽음: {records.Count:N0}건 — 적재 시작…");
                _eta.Reset();
                var (ins, upd) = _repo.UpsertRecords(records, OnProgress, token);
                return (records.Count, ins, upd);
            }, token);

            _log.UpdateLive("");
            _log.AppendLine($"\n  반입 완료 — 총 {result.Item1:N0}건 · 신규 {result.Item2:N0} · 갱신 {result.Item3:N0}");
            _statusLabel.Text = $"완료 · 신규 {result.Item2:N0} · 갱신 {result.Item3:N0}";
        }
        catch (OperationCanceledException) { _log.AppendLine("\n  중단됨. (커밋되지 않음 — 트랜잭션 롤백)"); _statusLabel.Text = "중단됨"; }
        catch (Exception ex) { _log.AppendLine($"\n[오류] {ex.GetType().Name}: {ex.Message}"); _statusLabel.Text = "오류"; }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnProgress(int done, int total)
    {
        var pct = total == 0 ? 100 : done * 100.0 / total;
        _log.UpdateLive($"  [반입 {ProgressBar(pct, 30)}] {done:N0}/{total:N0}{_eta.Format(done, total)}");
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
        _verifyBtn.Enabled = !busy;
        _importBtn.Enabled = !busy;
        _importBtn.Text = busy ? "반입 중…" : "반입";
        _stopBtn.Enabled = busy;
        _stopBtn.Text = "중지";
        _csvBox.Enabled = !busy;
    }

    private static string ProgressBar(double pct, int width)
    {
        var n = Math.Clamp((int)Math.Round(pct / 100.0 * width), 0, width);
        return new string('#', n) + new string('.', width - n);
    }

    public bool IsBusy => _busy;
    public void RequestStop() => OnStop();
}
