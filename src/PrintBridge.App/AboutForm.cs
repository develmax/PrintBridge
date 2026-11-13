using System.Diagnostics;

namespace PrintBridge;

internal sealed class AboutForm : Form
{
    internal const string RepositoryUrl = "https://github.com/develmax/PrintBridge";

    public AboutForm()
    {
        Text = T("About PrintBridge");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, Padding = new Padding(16)
        };
        Controls.Add(content);
        content.Controls.Add(new Label { AutoSize = true, Text = $"PrintBridge {Program.Version}" });
        content.Controls.Add(new Label { AutoSize = true, Text = $"{T("Author")}: develmax" });
        content.Controls.Add(new Label { AutoSize = true, Text = $"{T("License")}: {T("PrintBridge Source License")}" });
        AddLink(T("Source code and documentation"), RepositoryUrl);
        AddLink(T("Read the license"), RepositoryUrl + "/blob/main/LICENSE");

        var close = new Button { AutoSize = true, Text = T("Close"), DialogResult = DialogResult.OK };
        content.Controls.Add(close);
        AcceptButton = CancelButton = close;

        void AddLink(string text, string url)
        {
            var link = new LinkLabel { AutoSize = true, Text = text };
            link.LinkClicked += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            content.Controls.Add(link);
        }
    }
}
