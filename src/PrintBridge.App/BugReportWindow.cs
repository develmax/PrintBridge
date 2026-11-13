using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace PrintBridge;

internal sealed class BugReportWindow : AppDialog
{
    public BugReportWindow(string report) : base(T("Report a bug"), T("Report a bug"), 735, 470)
    {
        var content = new DockPanel();
        var notice = new TextBlock
        {
            Text = T("GitHub issues are public. Review the report below and include it if it helps."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(notice, Dock.Top);
        content.Children.Add(notice);
        var preview = new TextBox
        {
            Text = report, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FlowDirection = System.Windows.FlowDirection.LeftToRight
        };
        AutomationProperties.SetName(preview, T("Diagnostic report"));
        content.Children.Add(preview);
        Card.Child = content;

        Add(T("Copy report"), () => Clipboard.SetText(report));
        Add(T("Save report"), () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json", FileName = "PrintBridge-diagnostics.json"
            };
            if (dialog.ShowDialog(this) == true)
                File.WriteAllText(dialog.FileName, report);
        });
        Add(T("Open GitHub draft"), () =>
        {
            string body = $"PrintBridge version: {Program.Version}\n\n## What happened?\n\n## What did you expect?\n\n## Steps to reproduce\n\n## Diagnostics (optional)\n";
            string url = $"{AboutWindow.RepositoryUrl}/issues/new?body={Uri.EscapeDataString(body)}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }, primary: true);
        var close = AddAction(T("Close"));
        close.IsCancel = true;
        close.Click += (_, _) => Close();

        void Add(string title, Action action, bool primary = false)
        {
            AddAction(title, primary).Click += (_, _) =>
            {
                try { action(); }
                catch (Exception error) { MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
        }
    }
}
