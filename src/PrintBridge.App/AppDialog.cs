using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace PrintBridge;

internal class AppDialog : Window
{
    protected StackPanel Body { get; } = new();
    protected WrapPanel Actions { get; } = new();
    protected Border Card { get; }

    protected AppDialog(string title, string heading, double width, double height)
    {
        Title = title;
        Width = Math.Min(width, SystemParameters.WorkArea.Width);
        Height = Math.Min(height, SystemParameters.WorkArea.Height);
        ResizeMode = System.Windows.ResizeMode.NoResize;
        WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.Name);
        FlowDirection = CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
            ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 247, 251));
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 41, 67));
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 14;
        Icon = new BitmapImage(new Uri("pack://application:,,,/PrintBridge;component/Resources/DashboardIcon.png"));
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/PrintBridge;component/WindowStyles.xaml", UriKind.Relative)
        });

        var layout = new Grid { Margin = new Thickness(24, 18, 24, 16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = layout;

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new Image { Source = Icon, Width = 32, Height = 32 });
        header.Children.Add(new TextBlock
        {
            Text = heading, FontSize = 24, FontWeight = FontWeights.Bold,
            Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center
        });
        layout.Children.Add(header);

        Card = new Border
        {
            Style = (Style)FindResource("Card"), Margin = new Thickness(0),
            Child = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        Grid.SetRow(Card, 1);
        layout.Children.Add(Card);

        Actions.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        Actions.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(Actions, 2);
        layout.Children.Add(Actions);
    }

    public bool? ShowOwned(nint owner)
    {
        new WindowInteropHelper(this).Owner = owner;
        return ShowDialog();
    }

    protected Button AddAction(string label, bool primary = false)
    {
        var button = new Button { Content = label };
        if (primary)
        {
            button.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 77, 134));
            button.Foreground = System.Windows.Media.Brushes.White;
        }
        Actions.Children.Add(button);
        return button;
    }
}
