using System.Diagnostics;

namespace PrintBridge;

internal sealed class BugReportForm : Form
{
    public BugReportForm(string report)
    {
        Text = T("Report a bug");
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 370);
        MinimumSize = new Size(530, 320);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12)
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(content);
        content.Controls.Add(new Label
        {
            AutoSize = true,
            Text = T("GitHub issues are public. Review the report below and include it if it helps."),
            Margin = new Padding(3, 3, 3, 10)
        }, 0, 0);
        content.Controls.Add(new TextBox
        {
            Text = report, Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical, AccessibleName = T("Diagnostic report")
        }, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        content.Controls.Add(actions, 0, 2);
        Add(T("Copy report"), () => Clipboard.SetText(report));
        Add(T("Save report"), () =>
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json", FileName = "PrintBridge-diagnostics.json"
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                File.WriteAllText(dialog.FileName, report);
        });
        Add(T("Open GitHub draft"), () =>
        {
            string body = $"PrintBridge version: {Program.Version}\n\n## What happened?\n\n## What did you expect?\n\n## Steps to reproduce\n\n## Diagnostics (optional)\n";
            string url = $"{AboutForm.RepositoryUrl}/issues/new?body={Uri.EscapeDataString(body)}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        });
        var close = new Button { AutoSize = true, Text = T("Close"), DialogResult = DialogResult.Cancel };
        actions.Controls.Add(close);
        CancelButton = close;

        void Add(string label, Action action)
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            actions.Controls.Add(button);
        }
    }
}
