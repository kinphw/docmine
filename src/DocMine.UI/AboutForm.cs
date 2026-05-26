// About 모달 다이얼로그 — Python about.py:show_about 의 1:1 포팅.
//
// 표시:
//   - 앱명 (20pt bold)
//   - v{version} (회색)
//   - Author: {author}
//   - {tagline} (italic, 회색)
//   - 확인 버튼 (Enter / Escape 로 닫기)
//
// 버전/저자/제품명은 Assembly 의 attribute 에서 reflection 으로 읽음 —
// Directory.Build.props 의 <Version>/<Company>/<Product>/<InformationalVersion>
// 한 곳만 bump 하면 여기 자동 반영.

using System.Drawing;
using System.Reflection;

namespace DocMine.UI;

public sealed class AboutForm : Form
{
    private const string AppName  = "DocMine";
    private const string Tagline  = "github.com/kinphw/docmine";

    public static void Open(IWin32Window owner)
    {
        using var dlg = new AboutForm();
        dlg.ShowDialog(owner);
    }

    private AboutForm()
    {
        Text = $"About {AppName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 200);
        try { Icon = AppIcon.Build(); } catch { /* 아이콘 실패는 치명적 아님 */ }

        var asm = Assembly.GetEntryAssembly();
        var version = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm?.GetName().Version?.ToString()
            ?? "?";
        var author = asm?.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "kinphw";

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24),
        };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        body.Controls.Add(new Label
        {
            Text = AppName,
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0),
        }, 0, 0);

        body.Controls.Add(new Label
        {
            Text = $"v{version}",
            ForeColor = Color.FromArgb(0x88, 0x88, 0x88),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        }, 0, 1);

        body.Controls.Add(new Label
        {
            Text = $"Author: {author}",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0),
        }, 0, 2);

        body.Controls.Add(new Label
        {
            Text = Tagline,
            Font = new Font("Segoe UI", 9f, FontStyle.Italic),
            ForeColor = Color.FromArgb(0x55, 0x55, 0x55),
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 16),
        }, 0, 3);

        var okBtn = new Button
        {
            Text = "확인",
            Width = 80,
            Anchor = AnchorStyles.Right,
        };
        okBtn.Click += (_, _) => Close();
        AcceptButton = okBtn;
        CancelButton = okBtn;
        body.Controls.Add(okBtn, 0, 4);

        Controls.Add(body);
    }
}
