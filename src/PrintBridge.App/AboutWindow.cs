using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MessageBox = System.Windows.MessageBox;

namespace PrintBridge;

internal sealed class AboutWindow : AppDialog
{
    internal const string RepositoryUrl = "https://github.com/develmax/PrintBridge";

    public AboutWindow() : base(T("About PrintBridge"), T("About PrintBridge"), 500, 345)
    {
        AddLine($"PrintBridge {Program.Version}").FontWeight = FontWeights.SemiBold;
        AddLine($"{T("Author")}: develmax");
        AddLine($"{T("License")}: {T("PrintBridge Source License")}");
        AddLink(T("Source code and documentation"), RepositoryUrl);
        AddLink(T("Read the license"), RepositoryUrl + "/blob/main/LICENSE");

        var close = AddAction(T("Close"), primary: true);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();
    }

    private TextBlock AddLine(string text)
    {
        var line = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        Body.Children.Add(line);
        return line;
    }

    private void AddLink(string text, string url)
    {
        var link = new Hyperlink(new Run(text));
        link.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        AddLine("").Inlines.Add(link);
    }
}
