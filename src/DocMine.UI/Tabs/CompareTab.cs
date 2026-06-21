// CompareTab — 변경 전/후 HWP(X) 문서를 비교해 무엇이 바뀌었는지 표시.
//
// 회사에서 변경내용추적을 쓰지 않아 두 버전 사이 차이를 알기 어려운 문제를 해결.
//
// 세 가지 보기:
//   ㆍ 변경추적(기본): 변경 전/후를 한 흐름으로 병합 — 삭제=빨강 취소선, 추가=초록 밑줄.
//                      파일을 열지 않고도 화면에서 전체 문서를 읽으며 변경점을 따라간다.
//   ㆍ 변경 목록:      문단/표 단위로 "제2조 › 문단 5 수정" 처럼 변경만 위치와 함께 나열.
//   ㆍ 좌우대조:        본문 텍스트를 라인 단위로 좌우 대조 + 단어 하이라이트.
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

    // 무관 문서로 의심할 변경 비율 임계 — 넘으면 요약에 경고.
    private const double UnrelatedRatio = 0.85;

    private enum Mode { Track, Struct, Text }

    private readonly FileDropBox _oldBox, _newBox;
    private readonly Button  _compareBtn, _swapBtn, _saveBtn;
    private readonly RadioButton _modeTrack, _modeStruct, _modeText;
    private readonly CheckBox _ignoreWsBox, _changedOnlyBox;
    private readonly Label   _summary;

    private readonly TrackChangesView   _trackView  = new() { Dock = DockStyle.Fill, Visible = true  };
    private readonly SideBySideDiffView _textView   = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly StructureDiffView  _structView = new() { Dock = DockStyle.Fill, Visible = false };

    private readonly DocumentComparer          _comparer       = new();
    private readonly DocumentStructureComparer _structComparer = new();

    // 추출 결과 캐시 — 토글(공백 무시/변경만) 시 재추출 없이 재비교/재렌더.
    private string? _oldText, _newText;
    private DocStructure? _oldStruct, _newStruct;
    private SideBySideDiff? _lastTextDiff;
    private StructureDiff?  _lastStructDiff;
    private UnifiedDiff?    _lastUnified;

    private HwpWorkerClient? _activeWorker;   // 비교 취소용 — 중단 시 Dispose 로 워커 회수.
    private bool _busy;
    private bool _cancelRequested;

    public CompareTab() : base("⑤ 문서 비교")
    {
        // ── 입력 (변경 전 / 변경 후) — 좌우 두 개의 드롭 박스 ────────────
        // 파일을 끌어다 놓거나 클릭해 선택. 한 박스에 2개를 떨구면 전/후를 한꺼번에 채운다.
        const string hwpFilter = "한/글 문서 (*.hwp;*.hwpx)|*.hwp;*.hwpx|모든 파일 (*.*)|*.*";
        var srcGroup = new GroupBox { Text = "비교할 문서  (파일을 끌어다 놓거나 박스를 클릭해 선택)", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var srcGrid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        srcGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        srcGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        srcGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));

        _oldBox = new FileDropBox { Caption = "변경 전", DialogFilter = hwpFilter, Accept = IsSupported, Dock = DockStyle.Fill };
        _newBox = new FileDropBox { Caption = "변경 후", DialogFilter = hwpFilter, Accept = IsSupported, Dock = DockStyle.Fill };
        _oldBox.MultiDropped += (_, files) => FillPair(files);
        _newBox.MultiDropped += (_, files) => FillPair(files);

        srcGrid.Controls.Add(_oldBox, 0, 0);
        srcGrid.Controls.Add(_newBox, 1, 0);
        srcGroup.Controls.Add(srcGrid);

        // ── 동작 버튼 + 옵션 ────────────────────────────────────────────
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
        _compareBtn = new Button { Text = "비교", AutoSize = true };
        _swapBtn    = new Button { Text = "↔ 전/후 바꿈", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };

        _modeTrack  = new RadioButton { Text = "변경추적", AutoSize = true, Checked = true,  Margin = new Padding(16, 4, 0, 0) };
        _modeStruct = new RadioButton { Text = "변경 목록", AutoSize = true, Checked = false, Margin = new Padding(4, 4, 0, 0) };
        _modeText   = new RadioButton { Text = "좌우대조",   AutoSize = true, Checked = false, Margin = new Padding(4, 4, 0, 0) };

        _changedOnlyBox = new CheckBox { Text = "변경된 부분만", AutoSize = true, Checked = true, Enabled = false, Margin = new Padding(16, 4, 0, 0) };
        _ignoreWsBox    = new CheckBox { Text = "공백 변화 무시", AutoSize = true, Checked = true, Margin = new Padding(8, 4, 0, 0) };
        _saveBtn        = new Button   { Text = "결과 저장…", AutoSize = true, Enabled = false, Margin = new Padding(16, 0, 0, 0) };

        _compareBtn.Click += async (_, _) => { if (_busy) CancelCompare(); else await CompareAsync(); };
        _swapBtn.Click    += (_, _) => { (_oldBox.Path, _newBox.Path) = (_newBox.Path, _oldBox.Path); };
        _modeTrack.CheckedChanged  += (_, _) => OnModeChanged();
        _modeStruct.CheckedChanged += (_, _) => OnModeChanged();
        _modeText.CheckedChanged   += (_, _) => OnModeChanged();
        _changedOnlyBox.CheckedChanged += (_, _) => { var d = _lastTextDiff; if (d is not null && _modeText.Checked) _textView.Render(d, _changedOnlyBox.Checked); };
        _ignoreWsBox.CheckedChanged    += (_, _) => ReDiff();
        _saveBtn.Click    += async (_, _) => await SaveResultAsync();

        bar.Controls.Add(_compareBtn);
        bar.Controls.Add(_swapBtn);
        bar.Controls.Add(_modeTrack);
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

        // ── 결과 뷰 (변경추적/목록/좌우대조 토글) ───────────────────────
        var viewFrame = new GroupBox { Text = "비교 결과", Dock = DockStyle.Fill, Padding = new Padding(4) };
        viewFrame.Controls.Add(_trackView);
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

    private Mode CurrentMode =>
        _modeTrack.Checked ? Mode.Track : _modeStruct.Checked ? Mode.Struct : Mode.Text;

    private static bool IsSupported(string path) => SupportedExts.Contains(Path.GetExtension(path));

    // 한 박스에 2개를 떨궜을 때 — 변경 전/후를 한꺼번에 채운다(이름순으로 앞=전, 뒤=후).
    private void FillPair(string[] files)
    {
        var pick = files.Where(IsSupported)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Take(2).ToArray();
        if (pick.Length < 2) return;
        _oldBox.Path = pick[0];
        _newBox.Path = pick[1];
    }

    private void OnModeChanged()
    {
        // 모드 전환은 추출 방식이 다를 수 있어 결과를 비우고 재비교를 요청.
        _changedOnlyBox.Enabled = _modeText.Checked;
        _trackView.Visible  = _modeTrack.Checked;
        _structView.Visible = _modeStruct.Checked;
        _textView.Visible   = _modeText.Checked;
        _trackView.Clear();
        _structView.Clear();
        _textView.Clear();
        _oldText = _newText = null;
        _oldStruct = _newStruct = null;
        _lastTextDiff = null;
        _lastStructDiff = null;
        _lastUnified = null;
        _saveBtn.Enabled = false;
        _summary.Text = "비교 방식을 바꿨습니다 — [비교] 를 다시 누르세요.";
    }

    private async Task CompareAsync()
    {
        if (_busy) return;

        var oldPath = _oldBox.Path;
        var newPath = _newBox.Path;
        if (!Validate(oldPath, "변경 전") || !Validate(newPath, "변경 후")) return;

        var mode = CurrentMode;
        bool ignoreWs = _ignoreWsBox.Checked;

        _busy = true;
        _cancelRequested = false;
        SetBusyUi(true);
        _summary.Text = "추출·비교 중… (워커 프로세스 — [취소] 가능)";
        _trackView.Clear();
        _structView.Clear();
        _textView.Clear();
        _saveBtn.Enabled = false;

        var worker = new HwpWorkerClient();
        _activeWorker = worker;
        try
        {
            var outcome = await Task.Run(() => ExtractAndCompare(worker, oldPath, newPath, mode, ignoreWs));
            ApplyOutcome(outcome);
        }
        catch (Exception ex)
        {
            if (_cancelRequested)
                _summary.Text = "비교 취소됨.";
            else
            {
                _summary.Text = "비교 실패.";
                MessageBox.Show(this, ex.Message, "비교 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _activeWorker = null;
            try { worker.Dispose(); } catch { }
            _busy = false;
            SetBusyUi(false);
        }
    }

    private void CancelCompare()
    {
        if (!_busy) return;
        _cancelRequested = true;
        _summary.Text = "취소 중…";
        try { _activeWorker?.Dispose(); } catch { }   // 블로킹 ReadLine 을 EOF 로 깨움
    }

    private void ApplyOutcome(CompareOutcome o)
    {
        switch (o.Mode)
        {
            case Mode.Track:
                _oldStruct = o.OldStruct; _newStruct = o.NewStruct;
                _oldText = o.OldText; _newText = o.NewText;
                _lastUnified = o.Unified;
                _lastStructDiff = null; _lastTextDiff = null;

                _trackView.Visible = true; _structView.Visible = false; _textView.Visible = false;
                _trackView.Render(o.Unified!);
                _summary.Text = Note(o.Note) + FormatUnifiedSummary(o.Unified!, _trackView.WasCapped);
                _saveBtn.Enabled = o.Unified!.Summary.HasChanges;
                break;

            case Mode.Struct:
                _oldStruct = o.OldStruct; _newStruct = o.NewStruct; _lastStructDiff = o.StructDiff;
                _oldText = _newText = null; _lastTextDiff = null; _lastUnified = null;

                _structView.Visible = true; _trackView.Visible = false; _textView.Visible = false;
                _structView.Render(o.StructDiff!);
                _summary.Text = Note(o.Note) + FormatStructSummary(o.StructDiff!.Summary);
                _saveBtn.Enabled = o.StructDiff.Summary.HasChanges;
                break;

            default: // Text
                _oldText = o.OldText; _newText = o.NewText; _lastTextDiff = o.TextDiff;
                _oldStruct = _newStruct = null; _lastStructDiff = null; _lastUnified = null;

                _textView.Visible = true; _trackView.Visible = false; _structView.Visible = false;
                _textView.Render(o.TextDiff!, _changedOnlyBox.Checked);
                _summary.Text = Note(o.Note) + FormatTextSummary(o.TextDiff!.Summary);
                _saveBtn.Enabled = o.TextDiff.Summary.HasChanges;
                break;
        }
    }

    private static string Note(string? note) => note is null ? "" : "⚠ " + note + "      ";

    // ── 추출 + 비교 (백그라운드 스레드) ─────────────────────────────────
    private CompareOutcome ExtractAndCompare(HwpWorkerClient worker, string oldPath, string newPath, Mode mode, bool ignoreWs)
    {
        if (mode == Mode.Text)
        {
            var ot0 = worker.Extract(oldPath);
            var nt0 = worker.Extract(newPath);
            return CompareOutcome.Text(ot0, nt0, _comparer.CompareText(ot0, nt0, ignoreWs), null);
        }

        // Track / Struct — 구조 추출 시도, 실패 시 평문 폴백.
        try
        {
            var os = worker.ExtractStructure(oldPath);
            var ns = worker.ExtractStructure(newPath);
            if (mode == Mode.Track)
                return CompareOutcome.Track(os, ns, null, null, _structComparer.CompareUnified(os, ns, ignoreWs), null);
            return CompareOutcome.Struct(os, ns, _structComparer.Compare(os, ns, ignoreWs), null);
        }
        catch (Exception ex) when (!_cancelRequested)
        {
            var ot = worker.Extract(oldPath);
            var nt = worker.Extract(newPath);
            var why = $"구조 추출 실패 → 평문 비교로 대체 ({Trim(ex.Message)})";
            if (mode == Mode.Track)
                return CompareOutcome.Track(null, null, ot, nt, _comparer.CompareUnifiedText(ot, nt, ignoreWs), why);
            return CompareOutcome.Text(ot, nt, _comparer.CompareText(ot, nt, ignoreWs), why);
        }
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
        // 공백 무시 토글 — 캐시된 추출 결과로 재비교만(재추출 없음).
        switch (CurrentMode)
        {
            case Mode.Track:
                if (_oldStruct is not null && _newStruct is not null)
                {
                    var ud = _structComparer.CompareUnified(_oldStruct, _newStruct, _ignoreWsBox.Checked);
                    _lastUnified = ud; _trackView.Render(ud);
                    _summary.Text = FormatUnifiedSummary(ud, _trackView.WasCapped);
                    _saveBtn.Enabled = ud.Summary.HasChanges;
                }
                else if (_oldText is not null && _newText is not null)
                {
                    var ud = _comparer.CompareUnifiedText(_oldText, _newText, _ignoreWsBox.Checked);
                    _lastUnified = ud; _trackView.Render(ud);
                    _summary.Text = FormatUnifiedSummary(ud, _trackView.WasCapped);
                    _saveBtn.Enabled = ud.Summary.HasChanges;
                }
                break;

            case Mode.Struct:
                if (_oldStruct is null || _newStruct is null) return;
                var sd = _structComparer.Compare(_oldStruct, _newStruct, _ignoreWsBox.Checked);
                _lastStructDiff = sd; _structView.Render(sd);
                _summary.Text = FormatStructSummary(sd.Summary);
                _saveBtn.Enabled = sd.Summary.HasChanges;
                break;

            default:
                if (_oldText is null || _newText is null) return;
                var td = _comparer.CompareText(_oldText, _newText, _ignoreWsBox.Checked);
                _lastTextDiff = td; _textView.Render(td, _changedOnlyBox.Checked);
                _summary.Text = FormatTextSummary(td.Summary);
                _saveBtn.Enabled = td.Summary.HasChanges;
                break;
        }
    }

    // ── 요약 문구 ───────────────────────────────────────────────────────

    private static string Pages(int? oldP, int? newP)
    {
        if (oldP is null && newP is null) return "";
        string a = oldP?.ToString() ?? "?", b = newP?.ToString() ?? "?";
        return a == b ? $"{a}쪽   ·   " : $"{a} → {b}쪽   ·   ";
    }

    private static string FormatUnifiedSummary(UnifiedDiff d, bool capped)
    {
        var s = d.Summary;
        if (!s.HasChanges)
            return Pages(d.OldPages, d.NewPages) + "차이 없음 — 두 문서가 동일합니다.";

        int total = s.Inserted + s.Deleted + s.Modified + s.Unchanged;
        double ratio = total == 0 ? 0 : (double)(s.Inserted + s.Deleted + s.Modified) / total;
        string warn = ratio >= UnrelatedRatio
            ? $"⚠ 변경 {ratio * 100:0}% — 다른 문서일 수 있습니다.   "
            : "";
        string cap = capped ? "   (변경 과다 — 일부만 색 표시, 전체는 결과 저장)" : "";
        return warn + Pages(d.OldPages, d.NewPages)
            + $"변경 {s.ChangedLines:N0}건   —   추가 {s.Inserted:N0} · 삭제 {s.Deleted:N0} · 수정 {s.Modified:N0}" + cap;
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
        bool hasResult = (_modeTrack.Checked  && _lastUnified    is not null)
                      || (_modeStruct.Checked && _lastStructDiff is not null)
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
        var oldP = _oldBox.Path;
        var newP = _newBox.Path;

        // TXT — 인-프로세스로 즉시 저장.
        if (ext == ".txt")
        {
            try
            {
                var content = _modeTrack.Checked  ? BuildUnifiedText(_lastUnified!)
                            : _modeStruct.Checked ? BuildStructReport(_lastStructDiff!)
                            :                        BuildSideBySideText(_lastTextDiff!);
                File.WriteAllText(path, content, new UTF8Encoding(true));
                PromptOpen(path);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            return;
        }

        // HWP/HWPX — 워커 COM 으로 색상/취소선/밑줄 입힌 리포트 문서 생성.
        var format = ext == ".hwpx" ? "HWPX" : "HWP";
        var doc = _modeTrack.Checked
            ? ReportBuilder.FromUnified(_lastUnified!, oldP, newP, FormatUnifiedSummary(_lastUnified!, false))
            : _modeStruct.Checked
                ? ReportBuilder.FromStructure(_lastStructDiff!, oldP, newP, FormatStructSummary(_lastStructDiff!.Summary))
                : ReportBuilder.FromText(_lastTextDiff!, oldP, newP, FormatTextSummary(_lastTextDiff!.Summary));

        _busy = true;
        SetBusyUi(true);
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
            SetBusyUi(false);
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
            var a = Path.GetFileNameWithoutExtension(_oldBox.Path);
            var b = Path.GetFileNameWithoutExtension(_newBox.Path);
            return $"비교_{a}_vs_{b}.{ext}";
        }
        catch { return $"비교결과.{ext}"; }
    }

    private string Header(string summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DocMine 문서 비교 결과");
        sb.AppendLine($"#   변경 전: {_oldBox.Path}");
        sb.AppendLine($"#   변경 후: {_newBox.Path}");
        sb.AppendLine($"#   {summary}");
        sb.AppendLine(new string('=', 70));
        return sb.ToString();
    }

    // 변경추적 통합본 → 평문(취소선 불가라 마커로): 동일=그대로, 삭제=[-..-], 추가={+..+}
    private string BuildUnifiedText(UnifiedDiff diff)
    {
        var sb = new StringBuilder(Header(FormatUnifiedSummary(diff, false)));
        sb.AppendLine("#   기호:  [-삭제-]   {+추가+}");
        sb.AppendLine();
        foreach (var it in diff.Items)
        {
            if (it.Kind == UnifiedItemKind.Paragraph)
            {
                foreach (var r in it.Runs)
                    sb.Append(r.Kind switch
                    {
                        DiffChangeKind.Deleted  => $"[-{r.Text}-]",
                        DiffChangeKind.Inserted => $"{{+{r.Text}+}}",
                        _                       => r.Text,
                    });
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(it.LineKind switch
                {
                    DiffChangeKind.Inserted => $"［표 추가 {it.NewDims}］",
                    DiffChangeKind.Deleted  => $"［표 삭제 {it.OldDims}］",
                    DiffChangeKind.Modified => $"［표 변경 {it.OldDims} → {it.NewDims}］",
                    _                       => $"［표 {it.OldDims}］",
                });
                if (it.Cells is { Count: > 0 })
                    foreach (var c in it.Cells)
                        sb.AppendLine($"    [{c.Row + 1}행 {c.Col + 1}열]  [-{CellTxt(c.Old)}-]  →  {{+{CellTxt(c.New)}+}}");
            }
        }
        return sb.ToString();

        static string CellTxt(string s) => s.Length == 0 ? "(빈칸)" : s.Replace("\n", " ");
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

    private string BuildSideBySideText(SideBySideDiff diff)
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

    private void SetBusyUi(bool busy)
    {
        // 비교 중에는 [비교] 가 [취소] 로 바뀐다(버튼 자체는 계속 활성).
        _compareBtn.Text = busy ? "취소" : "비교";
        _swapBtn.Enabled = !busy;
        _oldBox.Enabled = !busy;
        _newBox.Enabled = !busy;
        _modeTrack.Enabled = !busy;
        _modeStruct.Enabled = !busy;
        _modeText.Enabled = !busy;
    }

    // ─ IBusyTab ─────────────────────────────────────────────────────────
    public bool IsBusy => _busy;
    public void RequestStop() { CancelCompare(); }

    // ─ 추출+비교 결과 ───────────────────────────────────────────────────
    private sealed record CompareOutcome(
        Mode Mode,
        DocStructure? OldStruct, DocStructure? NewStruct,
        StructureDiff? StructDiff,
        string? OldText, string? NewText, SideBySideDiff? TextDiff,
        UnifiedDiff? Unified,
        string? Note)
    {
        public static CompareOutcome Track(DocStructure? os, DocStructure? ns, string? ot, string? nt, UnifiedDiff u, string? note)
            => new(Mode.Track, os, ns, null, ot, nt, null, u, note);
        public static CompareOutcome Struct(DocStructure o, DocStructure n, StructureDiff d, string? note)
            => new(Mode.Struct, o, n, d, null, null, null, null, note);
        public static CompareOutcome Text(string o, string n, SideBySideDiff d, string? note)
            => new(Mode.Text, null, null, null, o, n, d, null, note);
    }
}
