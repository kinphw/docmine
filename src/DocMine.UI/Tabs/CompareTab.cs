// CompareTab — 변경 전/후 HWP(X) 문서를 비교해 무엇이 바뀌었는지 표시.
//
// 회사에서 변경내용추적을 쓰지 않아 두 버전 사이 차이를 알기 어려운 문제를 해결.
//
// 두 가지 비교 방식:
//   ㆍ 구조 비교(v2, 기본): 문단/표 단위로 정렬해 "제2조 › 문단 5 수정", "표 2행2열 변경"
//                           처럼 위치와 함께 변경 목록을 보여준다.
//   ㆍ 평문 좌우대조(v1):   본문 텍스트를 라인 단위로 좌우 대조 + 단어 하이라이트.
//
// 추출은 기존 --hwp-worker 재사용 — 모든 파일 내용 읽기는 워커(자식 프로세스)에서만.
//   구조 추출: 비DRM .hwpx 는 ZIP 직접, 바이너리 .hwp / DRM 은 COM SaveAs→HWPX 정규화.
//   정규화 실패(미지원/DLP 재암호화 등) 시 자동으로 평문 비교로 폴백하고 사유를 알린다.

using System.Diagnostics;
using System.Text;
using DocMine.Core.Diff;
using DocMine.UI.Worker;

namespace DocMine.UI.Tabs;

public sealed class CompareTab : TabPage, IBusyTab
{
    private static readonly HashSet<string> SupportedExts =
        new(StringComparer.OrdinalIgnoreCase) { ".hwp", ".hwpx" };

    private readonly TextBox _oldBox, _newBox;
    private readonly Button  _compareBtn, _swapBtn, _saveBtn;
    private readonly RadioButton _modeStruct, _modeText;
    private readonly CheckBox _ignoreWsBox, _changedOnlyBox;
    private readonly Label   _summary;

    private readonly SideBySideDiffView  _textView   = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly StructureDiffView   _structView = new() { Dock = DockStyle.Fill, Visible = true  };

    private readonly DocumentComparer          _comparer       = new();
    private readonly DocumentStructureComparer _structComparer = new();

    // 추출 결과 캐시 — 토글(공백 무시/변경만) 시 재추출 없이 재비교/재렌더.
    private string? _oldText, _newText;
    private DocStructure? _oldStruct, _newStruct;
    private SideBySideDiff? _lastTextDiff;
    private StructureDiff?  _lastStructDiff;
    private bool _busy;

    public CompareTab() : base("⑤ 문서 비교")
    {
        // ── 입력 경로 (변경 전 / 변경 후) ───────────────────────────────
        var srcGroup = new GroupBox { Text = "비교할 문서", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var srcGrid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        srcGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        srcGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        srcGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _oldBox = new TextBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _newBox = new TextBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        var oldBrowse = new Button { Text = "찾아보기…", AutoSize = true };
        var newBrowse = new Button { Text = "찾아보기…", AutoSize = true };
        oldBrowse.Click += (_, _) => BrowseInto(_oldBox);
        newBrowse.Click += (_, _) => BrowseInto(_newBox);

        // 드래그앤드롭 — 박스에 파일을 끌어다 놓으면 경로 자동 입력.
        // 한 박스에 2개를 떨구면 변경 전/후로 한꺼번에 채운다.
        EnableDrop(_oldBox, _newBox);
        EnableDrop(_newBox, _oldBox);

        srcGrid.Controls.Add(new Label { Text = "변경 전", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
        srcGrid.Controls.Add(_oldBox,   1, 0);
        srcGrid.Controls.Add(oldBrowse, 2, 0);
        srcGrid.Controls.Add(new Label { Text = "변경 후", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 1);
        srcGrid.Controls.Add(_newBox,   1, 1);
        srcGrid.Controls.Add(newBrowse, 2, 1);
        srcGroup.Controls.Add(srcGrid);

        // ── 동작 버튼 + 옵션 ────────────────────────────────────────────
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
        _compareBtn = new Button { Text = "비교", AutoSize = true };
        _swapBtn    = new Button { Text = "↔ 전/후 바꿈", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };

        _modeStruct = new RadioButton { Text = "구조 비교(문단·표)", AutoSize = true, Checked = true,  Margin = new Padding(16, 4, 0, 0) };
        _modeText   = new RadioButton { Text = "평문 좌우대조",       AutoSize = true, Checked = false, Margin = new Padding(4, 4, 0, 0) };

        _changedOnlyBox = new CheckBox { Text = "변경된 부분만", AutoSize = true, Checked = true, Enabled = false, Margin = new Padding(16, 4, 0, 0) };
        _ignoreWsBox    = new CheckBox { Text = "공백 변화 무시", AutoSize = true, Checked = true, Margin = new Padding(8, 4, 0, 0) };
        _saveBtn        = new Button   { Text = "결과 저장…", AutoSize = true, Enabled = false, Margin = new Padding(16, 0, 0, 0) };

        _compareBtn.Click += async (_, _) => await CompareAsync();
        _swapBtn.Click    += (_, _) => { (_oldBox.Text, _newBox.Text) = (_newBox.Text, _oldBox.Text); };
        _modeStruct.CheckedChanged += (_, _) => OnModeChanged();
        _modeText.CheckedChanged   += (_, _) => OnModeChanged();
        _changedOnlyBox.CheckedChanged += (_, _) => { var d = _lastTextDiff; if (d is not null && _modeText.Checked) _textView.Render(d, _changedOnlyBox.Checked); };
        _ignoreWsBox.CheckedChanged    += (_, _) => ReDiff();
        _saveBtn.Click    += async (_, _) => await SaveResultAsync();

        bar.Controls.Add(_compareBtn);
        bar.Controls.Add(_swapBtn);
        bar.Controls.Add(_modeStruct);
        bar.Controls.Add(_modeText);
        bar.Controls.Add(_changedOnlyBox);
        bar.Controls.Add(_ignoreWsBox);
        bar.Controls.Add(_saveBtn);

        _summary = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 22,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(2, 0, 0, 0),
            Text = "변경 전/후 문서를 선택하고 [비교] 를 누르세요. (.hwp / .hwpx)",
        };

        // ── 결과 뷰 (구조/평문 토글) ────────────────────────────────────
        var viewFrame = new GroupBox { Text = "비교 결과", Dock = DockStyle.Fill, Padding = new Padding(4) };
        viewFrame.Controls.Add(_textView);
        viewFrame.Controls.Add(_structView);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(srcGroup,  0, 0);
        root.Controls.Add(bar,       0, 1);
        root.Controls.Add(_summary,  0, 2);
        root.Controls.Add(viewFrame, 0, 3);
        Controls.Add(root);
    }

    private void EnableDrop(TextBox box, TextBox other)
    {
        box.AllowDrop = true;
        box.DragEnter += (_, e) =>
        {
            e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                ? DragDropEffects.Copy : DragDropEffects.None;
        };
        box.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            box.Text = files[0];
            if (files.Length >= 2 && other.Text.Trim().Length == 0) other.Text = files[1];
        };
    }

    private void BrowseInto(TextBox box)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "비교할 문서 선택",
            Filter = "한/글 문서 (*.hwp;*.hwpx)|*.hwp;*.hwpx|모든 파일 (*.*)|*.*",
        };
        if (box.Text.Trim().Length > 0)
        {
            try { dlg.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(box.Text.Trim())); } catch { }
        }
        if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
    }

    private void OnModeChanged()
    {
        // 모드 전환은 추출 방식이 다르므로 결과를 비우고 재비교를 요청.
        _changedOnlyBox.Enabled = _modeText.Checked;
        _textView.Visible   = _modeText.Checked;
        _structView.Visible = _modeStruct.Checked;
        _textView.Clear();
        _structView.Clear();
        _oldText = _newText = null;
        _oldStruct = _newStruct = null;
        _lastTextDiff = null;
        _lastStructDiff = null;
        _saveBtn.Enabled = false;
        _summary.Text = "비교 방식을 바꿨습니다 — [비교] 를 다시 누르세요.";
    }

    private async Task CompareAsync()
    {
        if (_busy) return;

        var oldPath = _oldBox.Text.Trim();
        var newPath = _newBox.Text.Trim();
        if (!Validate(oldPath, "변경 전") || !Validate(newPath, "변경 후")) return;

        bool structured = _modeStruct.Checked;
        bool ignoreWs   = _ignoreWsBox.Checked;

        _busy = true;
        SetControlsEnabled(false);
        _summary.Text = structured ? "구조 추출·비교 중… (워커 프로세스)" : "본문 추출·비교 중… (워커 프로세스)";
        _textView.Clear();
        _structView.Clear();
        _saveBtn.Enabled = false;

        try
        {
            var outcome = await Task.Run(() => ExtractAndCompare(oldPath, newPath, structured, ignoreWs));
            ApplyOutcome(outcome);
        }
        catch (Exception ex)
        {
            _summary.Text = "비교 실패.";
            MessageBox.Show(this, ex.Message, "비교 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
        }
    }

    private void ApplyOutcome(CompareOutcome o)
    {
        if (o.Structured)
        {
            _oldStruct = o.OldStruct; _newStruct = o.NewStruct; _lastStructDiff = o.StructDiff;
            _oldText = _newText = null; _lastTextDiff = null;

            _structView.Visible = true; _textView.Visible = false;
            _structView.Render(o.StructDiff!);
            _summary.Text = FormatStructSummary(o.StructDiff!.Summary);
            _saveBtn.Enabled = o.StructDiff.Summary.HasChanges;
        }
        else
        {
            _oldText = o.OldText; _newText = o.NewText; _lastTextDiff = o.TextDiff;
            _oldStruct = _newStruct = null; _lastStructDiff = null;

            _textView.Visible = true; _structView.Visible = false;
            _textView.Render(o.TextDiff!, _changedOnlyBox.Checked);
            var note = o.Note is null ? "" : "⚠ " + o.Note + "      ";
            _summary.Text = note + FormatTextSummary(o.TextDiff!.Summary);
            _saveBtn.Enabled = o.TextDiff.Summary.HasChanges;
        }
    }

    // ── 추출 + 비교 (백그라운드 스레드) ─────────────────────────────────
    private CompareOutcome ExtractAndCompare(string oldPath, string newPath, bool structured, bool ignoreWs)
    {
        using var worker = new HwpWorkerClient();

        if (structured)
        {
            try
            {
                var os = worker.ExtractStructure(oldPath);
                var ns = worker.ExtractStructure(newPath);
                var sd = _structComparer.Compare(os, ns, ignoreWs);
                return CompareOutcome.Struct(os, ns, sd);
            }
            catch (Exception ex)
            {
                // 정규화/구조 추출 실패 → 같은 워커로 평문 추출 폴백.
                var ot = worker.Extract(oldPath);
                var nt = worker.Extract(newPath);
                var td = _comparer.CompareText(ot, nt, ignoreWs);
                return CompareOutcome.Text(ot, nt, td, $"구조 추출 실패 → 평문 비교로 대체 ({Trim(ex.Message)})");
            }
        }

        var o = worker.Extract(oldPath);
        var n = worker.Extract(newPath);
        return CompareOutcome.Text(o, n, _comparer.CompareText(o, n, ignoreWs), null);
    }

    private static string Trim(string s) => s.Length <= 120 ? s : s[..120] + "…";

    private bool Validate(string path, string label)
    {
        if (path.Length == 0) { MessageBox.Show(this, $"'{label}' 문서를 선택하세요.", "경로 누락"); return false; }
        if (!File.Exists(path)) { MessageBox.Show(this, $"'{label}' 파일을 찾을 수 없습니다:\n{path}", "파일 없음"); return false; }
        if (!SupportedExts.Contains(Path.GetExtension(path)))
        {
            MessageBox.Show(this, $"'{label}' 은 지원 형식이 아닙니다 (.hwp / .hwpx 만).\n{path}", "형식 오류");
            return false;
        }
        return true;
    }

    private void ReDiff()
    {
        if (_modeStruct.Checked)
        {
            if (_oldStruct is null || _newStruct is null) return;
            var sd = _structComparer.Compare(_oldStruct, _newStruct, _ignoreWsBox.Checked);
            _lastStructDiff = sd;
            _structView.Render(sd);
            _summary.Text = FormatStructSummary(sd.Summary);
            _saveBtn.Enabled = sd.Summary.HasChanges;
        }
        else
        {
            if (_oldText is null || _newText is null) return;
            var td = _comparer.CompareText(_oldText, _newText, _ignoreWsBox.Checked);
            _lastTextDiff = td;
            _textView.Render(td, _changedOnlyBox.Checked);
            _summary.Text = FormatTextSummary(td.Summary);
            _saveBtn.Enabled = td.Summary.HasChanges;
        }
    }

    private static string FormatStructSummary(DiffSummary s)
        => s.HasChanges
            ? $"변경 {s.ChangedLines:N0}건   —   추가 {s.Inserted:N0} · 삭제 {s.Deleted:N0} · 수정 {s.Modified:N0}  (문단·표 단위)"
            : "차이 없음 — 두 문서의 구조(문단·표)가 동일합니다.";

    private static string FormatTextSummary(DiffSummary s)
        => s.HasChanges
            ? $"변경 {s.ChangedLines:N0}줄   —   추가 {s.Inserted:N0} · 삭제 {s.Deleted:N0} · 수정 {s.Modified:N0}"
            : "차이 없음 — 두 문서의 본문 텍스트가 동일합니다.";

    // ── 결과 저장 — HWP/HWPX(색상 리포트, COM) 또는 TXT ─────────────────
    private async Task SaveResultAsync()
    {
        if (_busy) return;
        bool hasResult = (_modeStruct.Checked && _lastStructDiff is not null)
                      || (_modeText.Checked   && _lastTextDiff   is not null);
        if (!hasResult) return;

        using var dlg = new SaveFileDialog
        {
            Title = "비교 결과 저장",
            DefaultExt = "hwp",
            FileName = BuildDefaultSaveName("hwp"),
            Filter = "한/글 문서 (*.hwp)|*.hwp|한/글 문서 (*.hwpx)|*.hwpx|텍스트 (*.txt)|*.txt",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var path = dlg.FileName;
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // TXT — 인-프로세스로 즉시 저장.
        if (ext == ".txt")
        {
            try
            {
                var content = _modeStruct.Checked
                    ? BuildStructReport(_lastStructDiff!)
                    : BuildUnifiedText(_lastTextDiff!);
                File.WriteAllText(path, content, new UTF8Encoding(true));
                PromptOpen(path);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            return;
        }

        // HWP/HWPX — 워커 COM 으로 색상 입힌 리포트 문서 생성.
        var format = ext == ".hwpx" ? "HWPX" : "HWP";
        var oldP = _oldBox.Text.Trim();
        var newP = _newBox.Text.Trim();
        var doc = _modeStruct.Checked
            ? ReportBuilder.FromStructure(_lastStructDiff!, oldP, newP, FormatStructSummary(_lastStructDiff!.Summary))
            : ReportBuilder.FromText(_lastTextDiff!, oldP, newP, FormatTextSummary(_lastTextDiff!.Summary));

        _busy = true;
        SetControlsEnabled(false);
        _saveBtn.Enabled = false;
        var prev = _summary.Text;
        _summary.Text = "한/글 리포트 생성 중… (COM)";
        try
        {
            await Task.Run(() =>
            {
                using var worker = new HwpWorkerClient();
                worker.GenerateReport(doc, path, format);
            });
            _summary.Text = prev;
            PromptOpen(path);
        }
        catch (Exception ex)
        {
            _summary.Text = prev;
            MessageBox.Show(this,
                "한/글 리포트 생성에 실패했습니다.\n" +
                "(한/글 미설치 또는 COM 거부 가능성 — TXT 저장은 항상 가능합니다.)\n\n" + ex.Message,
                "리포트 저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
            _saveBtn.Enabled = true;
        }
    }

    private void PromptOpen(string path)
    {
        if (MessageBox.Show(this, "저장했습니다. 파일을 여시겠습니까?", "저장 완료",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "열기 실패"); }
    }

    private string BuildDefaultSaveName(string ext)
    {
        try
        {
            var a = Path.GetFileNameWithoutExtension(_oldBox.Text.Trim());
            var b = Path.GetFileNameWithoutExtension(_newBox.Text.Trim());
            return $"비교_{a}_vs_{b}.{ext}";
        }
        catch { return $"비교결과.{ext}"; }
    }

    private string Header(string summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DocMine 문서 비교 결과");
        sb.AppendLine($"#   변경 전: {_oldBox.Text.Trim()}");
        sb.AppendLine($"#   변경 후: {_newBox.Text.Trim()}");
        sb.AppendLine($"#   {summary}");
        sb.AppendLine(new string('=', 70));
        return sb.ToString();
    }

    private string BuildStructReport(StructureDiff diff)
    {
        var sb = new StringBuilder(Header(FormatStructSummary(diff.Summary)));
        foreach (var ch in diff.Changes)
        {
            var tag = ch.Kind switch
            {
                StructChangeKind.Inserted          => "추가",
                StructChangeKind.Deleted           => "삭제",
                StructChangeKind.ModifiedParagraph => "수정",
                StructChangeKind.ModifiedTable     => "표변경",
                _ => "?",
            };
            sb.AppendLine($"● {tag}  {ch.Location}");
            switch (ch.Kind)
            {
                case StructChangeKind.Inserted: sb.AppendLine($"   + {ch.NewText}"); break;
                case StructChangeKind.Deleted:  sb.AppendLine($"   - {ch.OldText}"); break;
                case StructChangeKind.ModifiedParagraph:
                    sb.AppendLine($"   - {ch.OldText}");
                    sb.AppendLine($"   + {ch.NewText}");
                    break;
                case StructChangeKind.ModifiedTable:
                    sb.AppendLine($"   표 크기 {ch.OldDims} → {ch.NewDims}");
                    if (ch.Cells is { Count: > 0 })
                        foreach (var c in ch.Cells)
                            sb.AppendLine($"   [{c.Row + 1}행 {c.Col + 1}열]  {Cell(c.Old)} → {Cell(c.New)}");
                    break;
            }
            sb.AppendLine();
        }
        return sb.ToString();

        static string Cell(string s) => s.Length == 0 ? "(빈칸)" : s.Replace("\n", " ");
    }

    private string BuildUnifiedText(SideBySideDiff diff)
    {
        var sb = new StringBuilder(Header(FormatTextSummary(diff.Summary)));
        sb.AppendLine("#   기호:  -삭제   +추가   ~수정(전/후)   (공백)동일");
        int n = Math.Min(diff.Left.Count, diff.Right.Count);
        for (int i = 0; i < n; i++)
        {
            var l = diff.Left[i];
            var r = diff.Right[i];
            switch (r.Kind)
            {
                case DiffChangeKind.Unchanged: sb.Append("  ").AppendLine(r.Text); break;
                case DiffChangeKind.Inserted:  sb.Append("+ ").AppendLine(r.Text); break;
                case DiffChangeKind.Modified:
                    sb.Append("~-").AppendLine(l.Text);
                    sb.Append("~+").AppendLine(r.Text);
                    break;
            }
            if (r.Kind != DiffChangeKind.Modified && l.Kind == DiffChangeKind.Deleted)
                sb.Append("- ").AppendLine(l.Text);
        }
        return sb.ToString();
    }

    private void SetControlsEnabled(bool on)
    {
        _compareBtn.Enabled = on;
        _compareBtn.Text = on ? "비교" : "비교 중…";
        _swapBtn.Enabled = on;
        _oldBox.Enabled = on;
        _newBox.Enabled = on;
        _modeStruct.Enabled = on;
        _modeText.Enabled = on;
    }

    // ─ IBusyTab ─────────────────────────────────────────────────────────
    public bool IsBusy => _busy;
    public void RequestStop() { /* 비교는 짧고 워커가 Dispose 로 회수됨 */ }

    // ─ 추출+비교 결과 ───────────────────────────────────────────────────
    private sealed record CompareOutcome(
        bool Structured,
        DocStructure? OldStruct, DocStructure? NewStruct, StructureDiff? StructDiff,
        string? OldText, string? NewText, SideBySideDiff? TextDiff,
        string? Note)
    {
        public static CompareOutcome Struct(DocStructure o, DocStructure n, StructureDiff d)
            => new(true, o, n, d, null, null, null, null);
        public static CompareOutcome Text(string o, string n, SideBySideDiff d, string? note)
            => new(false, null, null, null, o, n, d, note);
    }
}
