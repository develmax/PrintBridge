using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using IWin32Window = System.Windows.Forms.IWin32Window;

namespace PrintBridge;

internal partial class PreferencesWindow : Window
{
    private sealed record AdapterChoice(Guid? Id, string Name);
    private sealed class DialogOwner(Window window) : IWin32Window
    {
        public IntPtr Handle { get; private set; } = new WindowInteropHelper(window).Handle;

        public void Close() => Handle = IntPtr.Zero;
    }

    private readonly AppSettings initial;
    private readonly ComboBox language = new() { DisplayMemberPath = nameof(LanguageOption.Name), SelectedValuePath = nameof(LanguageOption.Code) };
    private readonly ComboBox adapter = new() { DisplayMemberPath = nameof(AdapterChoice.Name), SelectedValuePath = nameof(AdapterChoice.Id) };
    private readonly CheckBox startup, hidden, notify, search, verbose, autoUpdate;
    private readonly TextBox interval, local, connect, idle, limit;
    private readonly Dictionary<TextBox, (int Min, int Max)> ranges = new();

    public AppSettings Value => initial with
    {
        StartHidden = hidden.IsChecked == true,
        Notify = notify.IsChecked == true,
        AutoDiscover = search.IsChecked == true,
        Verbose = verbose.IsChecked == true,
        AutoUpdate = autoUpdate.IsChecked == true,
        Language = (string)language.SelectedValue,
        Adapter = ((AdapterChoice)adapter.SelectedItem).Id,
        IntervalSeconds = Read(interval),
        LocalPort = Read(local),
        ConnectSeconds = Read(connect),
        IdleSeconds = Read(idle),
        MaxConnections = Read(limit)
    };

    public bool StartupEnabled => startup.IsChecked == true;

    public PreferencesWindow(AppSettings settings, bool runAtStartup, IReadOnlyList<LanAdapter> adapters,
        Action clean, Func<IWin32Window, Task> checkUpdates)
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        initial = settings;
        Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.Name);
        if (CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft)
            FlowDirection = System.Windows.FlowDirection.RightToLeft;
        Title = T("PrintBridge Settings");
        Heading.Text = T("Settings");
        GeneralTab.Header = T("General");
        NetworkTab.Header = T("Local network");
        MaintenanceTab.Header = T("Updates and logs");

        language.ItemsSource = Languages;
        language.SelectedValue = Normalize(settings.Language);
        Row(General, T("Language"), language);
        startup = Check(General, T("Start with Windows"), runAtStartup);
        hidden = Check(General, T("Start minimized to the tray"), settings.StartHidden);
        notify = Check(General, T("Show tray notifications"), settings.Notify);

        var choices = new List<AdapterChoice> { new(null, T("Automatic")) };
        choices.AddRange(adapters.DistinctBy(item => item.Id).Select(item => new AdapterChoice(item.Id, item.ToString())));
        if (settings.Adapter is Guid id && !choices.Any(item => item.Id == id))
            choices.Add(new(id, T("Saved adapter unavailable")));
        adapter.ItemsSource = choices;
        adapter.SelectedItem = choices.First(item => item.Id == settings.Adapter);
        Row(Network, T("Network adapter"), adapter);
        search = Check(Network, T("Find new IP addresses automatically"), settings.AutoDiscover);
        interval = Number(T("Idle check interval (seconds)"), settings.IntervalSeconds, 15, 3600);
        local = Number(T("Local port"), settings.LocalPort, 1024, 65535);
        local.IsEnabled = settings.Profile == null;
        connect = Number(T("Connect timeout (seconds)"), settings.ConnectSeconds, 1, 30);
        idle = Number(T("Idle timeout (seconds)"), settings.IdleSeconds, 15, 1800);
        limit = Number(T("Concurrent connections"), settings.MaxConnections, 1, 16);
        Row(Network, T("Print protocol"), new TextBlock { Text = "RAW TCP/9100", FlowDirection = System.Windows.FlowDirection.LeftToRight });
        if (settings.Profile is { } profile)
            Row(Network, T("Queue and identifier"), new TextBlock
            {
                Text = profile.Print.Queue + "\n" + (profile.Identity.Mac ?? profile.Identity.Serial ?? profile.Identity.DeviceId),
                TextWrapping = TextWrapping.Wrap
            });

        autoUpdate = Check(Maintenance, T("Automatically check for and install updates"), settings.AutoUpdate);
        var update = ActionButton(T("Check for updates now"));
        update.Click += async (_, _) =>
        {
            var owner = new DialogOwner(this);
            void OnClosed(object? sender, EventArgs args) => owner.Close();
            Closed += OnClosed;
            update.IsEnabled = false;
            try
            {
                await checkUpdates(owner);
            }
            finally
            {
                Closed -= OnClosed;
                update.IsEnabled = true;
            }
        };
        verbose = Check(Maintenance, T("Verbose logging"), settings.Verbose);
        var clear = ActionButton(T("Delete old logs"));
        clear.Click += (_, _) =>
        {
            try
            {
                clean();
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        SaveButton.Content = T("Save");
        SaveButton.Click += (_, _) => DialogResult = true;
        CancelButton.Content = T("Cancel");
    }

    private static CheckBox Check(Panel section, string title, bool enabled)
    {
        var control = new CheckBox { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap }, IsChecked = enabled };
        section.Children.Add(control);
        return control;
    }

    private TextBox Number(string title, int value, int min, int max)
    {
        var control = new TextBox
        {
            Text = Math.Clamp(value, min, max).ToString(CultureInfo.InvariantCulture),
            Width = 96,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            FlowDirection = System.Windows.FlowDirection.LeftToRight,
            ToolTip = $"{min}–{max}"
        };
        ranges.Add(control, (min, max));
        control.TextChanged += (_, _) =>
        {
            if (Valid(control, ranges[control]))
                control.ClearValue(BorderBrushProperty);
            else
                control.BorderBrush = Brushes.Firebrick;
            SaveButton.IsEnabled = ranges.All(field => Valid(field.Key, field.Value));
        };
        Row(Network, title, control);
        return control;
    }

    private static bool Valid(TextBox field, (int Min, int Max) range) =>
        int.TryParse(field.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
        && value >= range.Min && value <= range.Max;

    private static int Read(TextBox field) => int.Parse(field.Text, NumberStyles.None, CultureInfo.InvariantCulture);

    private Button ActionButton(string title)
    {
        var button = new Button { Content = title, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        Maintenance.Children.Add(button);
        return button;
    }

    private static void Row(Panel section, string title, UIElement value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetLabeledBy(value, label);
        Grid.SetColumn(value, 1);
        row.Children.Add(label);
        row.Children.Add(value);
        section.Children.Add(row);
    }
}
