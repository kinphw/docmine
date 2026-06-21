// ImportTab — 반입. 반출(본문 포함 전송 CSV)의 역연산이자 거울상 UX.
//
// 반출이 "현재 DB ↔ 외부 매니페스트" 라면, 반입은 "외부 CSV ↔ 현재 env" 다.
//   목록(base, 액션 대상) = 반입 CSV 레코드(본문 포함)
//   대조                  = 현재 env (폴더+파일명 키)
//   두 컬럼: CSV / 현재환경.  CSV✓ 현재환경✗ = 신규(삽입), 양쪽 = 갱신, 현재환경만 = 유령(정보).
// 선택 → (directory,filename) UNIQUE 기준 upsert. 멱등 — 같은 CSV 재반입해도 안전.
//
// 적재(②)와의 차이: 적재는 실제 파일 재파싱(파일 필요), 반입은 파싱된 레코드 직접 이전.

using DocMine.Core.Config;
using DocMine.Core.Db;
using DocMine.Core.Pipeline;

namespace DocMine.UI.Tabs;

public sealed class ImportTab : TabPage, IBusyTab
{
    private DocumentRepository _repo;
    private readonly TextBox _csvBox;
    private readonly Button _loadBtn, _importBtn, _stopBtn;
    private readonly Label _statusLabel;
    private readonly EnvCompareList _cmp;
    private readonly LogPane _log;
    private readonly ProgressEta _eta = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    private List<DocRecord> _records = new();   // 로드한 CSV 레코드(본문 포함)

    public ImportTab() : base("반입")
    {
        _repo = new DocumentRepository(AppConfig.Current);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ── 상단 ────────────────────────────────────────────────────────
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.Controls.Add(new Label
        {
            Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Gray,
            Text = "다른 환경의 '반출'(본문 포함 CSV)을 불러와 현재 env 와 대조한 뒤, 선택 항목만 적재합니다.\n" +
                   "파일 재파싱 없이 레코드를 그대로 이전 · (폴더+파일명) 기준 upsert(신규 삽입/기존 갱신, 재반입 안전).",
        }, 0, 0);

        var csvRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        csvRow.Controls.Add(new Label { Text = "반입 CSV:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _csvBox = new TextBox { Width = 380 };
        csvRow.Controls.Add(_csvBox);
        var browseBtn = new Button { Text = "찾아보기…", AutoSize = true, Margin = new Padding(4, 2, 0, 0) };
        browseBtn.Click += (_, _) => BrowseCsv();
        csvRow.Controls.Add(browseBtn);
        _loadBtn = new Button { Text = "불러오기 (현재환경 대조)", AutoSize = true, Margin = new Padding(8, 2, 0, 0) };
        _loadBtn.Click += async (_, _) => await LoadAsync();
        csvRow.Controls.Add(_loadBtn);
        _statusLabel = new Label { Text = "", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(12, 6, 0, 0) };
        csvRow.Controls.Add(_statusLabel);
        top.Controls.Add(csvRow, 0, 1);
        root.Controls.Add(top, 0, 0);

        // ── 합집합 대조 리스트 (반출과 공유) ──────────────────────────
        _cmp = new EnvCompareList { Dock = DockStyle.Fill };
        _cmp.Configure("기적재",
            new EnvCompareList.ColumnDef("폴더", 300, HorizontalAlignment.Left),
            new EnvCompareList.ColumnDef("파일명", 240, HorizontalAlignment.Left),
            new EnvCompareList.ColumnDef("확장자", 60, HorizontalAlignment.Left),
            new EnvCompareList.ColumnDef("크기", 90, HorizontalAlignment.Right),
            new EnvCompareList.ColumnDef("본문", 50, HorizontalAlignment.Center),
            new EnvCompareList.ColumnDef("상태", 70, HorizontalAlignment.Left));
        root.Controls.Add(_cmp, 0, 1);

        // ── 로그 ──────────────────────────────────────────────────────────
        _log = new LogPane { Dock = DockStyle.Fill };
        var logFrame = new GroupBox { Text = "로그", Dock = DockStyle.Fill, Height = 110, Padding = new Padding(4) };
        logFrame.Controls.Add(_log);
        root.Controls.Add(logFrame, 0, 2);

        // ── 하단 액션 (Dock=Bottom) ──────────────────────────────────
        var bot = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 1, Padding = new Padding(8, 4, 8, 4),
        };
        bot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var btnFlow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _stopBtn = new Button { Text = "중지", AutoSize = true, Enabled = false, Margin = new Padding(6, 0, 0, 0) };
        _stopBtn.Click += (_, _) => OnStop();
        _importBtn = new Button { Text = "선택 항목 반입", AutoSize = true, Enabled = false };
        _importBtn.Click += async (_, _) => await ImportAsync();
        btnFlow.Controls.Add(_stopBtn);
        btnFlow.Controls.Add(_importBtn);
        _cmp.SelectionChanged += () => _importBtn.Enabled = _cmp.SelectedCount > 0 && !_busy;
        bot.Controls.Add(btnFlow, 0, 0);

        Controls.Add(root);
        Controls.Add(bot);

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

    // ─ 로드 + 현재환경 대조 ──────────────────────────────────────────
    private async Task LoadAsync()
    {
        if (_busy) return;
        var csv = _csvBox.Text.Trim();
        if (string.IsNullOrEmpty(csv) || !File.Exists(csv))
        {
            MessageBox.Show(this, $"반입할 CSV 파일을 찾을 수 없습니다:\n{csv}", "CSV 없음");
            return;
        }

        _loadBtn.Enabled = false;
        _statusLabel.Text = "불러오는 중… (CSV + 현재환경)";
        try
        {
            var (records, envKeys) = await Task.Run(() =>
            {
                var recs = DocTransferCsv.Read(csv);
                var keys = _repo.LoadAllKeys();
                return (recs, keys);
            });
            _records = records;
            BuildUnion(envKeys);

            var withBody = records.Count(r => !string.IsNullOrEmpty(r.BodyText));
            _log.AppendLine($"[불러오기] {Path.GetFileName(csv)} → CSV {records.Count:N0}건 (본문 {withBody:N0}) · 현재환경 {envKeys.Count:N0}건");
            if (withBody == 0 && records.Count > 0)
                _log.AppendLine("  ⚠ 본문이 하나도 없습니다 — 메타데이터만 든 CSV(manifest?)일 수 있습니다.");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "오류";
            MessageBox.Show(this, ex.Message, "불러오기 오류");
            _log.AppendLine($"[오류] {ex.Message}");
        }
        finally { _loadBtn.Enabled = true; }
    }

    private void BuildUnion(IReadOnlyList<(string Dir, string Fn)> envKeys)
    {
        // CSV(base) 전체 표시 + 각 행의 기적재(현재 env 보유) 여부. env 에만 있는 건 안 보인다.
        var union = EnvCompare.Build(_records, r => (r.Directory, r.Filename), envKeys, nameOnly: false);
        var rows = union.Base.Select(m => RowOf(m.Item, m.InCompare)).ToList();
        _cmp.SetRows(rows, compareLoaded: true);

        int total = rows.Count;
        int both  = union.Base.Count(m => m.InCompare);
        _statusLabel.Text = $"CSV {total:N0} · 신규 {total - both:N0} · 기적재(갱신) {both:N0}";
    }

    private static CompareRow RowOf(DocRecord r, bool inCompare) => new()
    {
        Key = (r.Directory, r.Filename),
        InCompare = inCompare,
        Cells = new[]
        {
            r.Directory, r.Filename, r.Extension ?? "", SizeStr(r.FileSize),
            string.IsNullOrEmpty(r.BodyText) ? "" : "✓", r.ParseStatus ?? "",
        },
        Item = r,
    };

    private static string SizeStr(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB" : $"{bytes / 1024.0:F0} KB";

    // ─ 반입(upsert) ──────────────────────────────────────────────────
    private async Task ImportAsync()
    {
        if (_busy) return;
        var selected = _cmp.SelectedItems.OfType<DocRecord>().ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "반입할 행을 하나 이상 선택하세요.", "선택 없음"); return; }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var cfg = AppConfig.Current;
        _log.AppendLine($"  DB: {cfg.DbUser}@{cfg.DbHost}:{cfg.DbPort}/{cfg.DbName} (table={cfg.DbTable})");
        _log.AppendLine($"  반입 대상: {selected.Count:N0}건 (선택) — 적재 시작…");
        _eta.Reset();

        var token = _cts.Token;
        try
        {
            var (ins, upd) = await Task.Run(() => _repo.UpsertRecords(selected, OnProgress, token), token);
            _log.UpdateLive("");
            _log.AppendLine($"\n  반입 완료 — 신규 {ins:N0} · 갱신 {upd:N0}");
            _statusLabel.Text = $"완료 · 신규 {ins:N0} · 갱신 {upd:N0}";

            // 적재 후 현재환경 다시 대조(방금 넣은 건 이제 양쪽에 표시).
            var envKeys = await Task.Run(() => _repo.LoadAllKeys());
            BuildUnion(envKeys);
        }
        catch (OperationCanceledException) { _log.AppendLine("\n  중단됨. (트랜잭션 롤백 — 반영 안 됨)"); _statusLabel.Text = "중단됨"; }
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
        _loadBtn.Enabled = !busy;
        _importBtn.Enabled = !busy && _cmp.SelectedCount > 0;
        _importBtn.Text = busy ? "반입 중…" : "선택 항목 반입";
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
