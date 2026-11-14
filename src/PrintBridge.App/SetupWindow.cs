using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using RadioButton = System.Windows.Controls.RadioButton;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace PrintBridge;

internal sealed record SetupSelection(LanAdapter Adapter, PrinterQueue Queue, NetworkPrinter Device, bool Separate);

internal sealed class SetupWindow : AppDialog
{
    private sealed record AdapterChoice(Guid? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record DeviceChoice(LanAdapter Adapter, NetworkPrinter Printer)
    {
        public override string ToString() => $"{Printer} · {Adapter.ConnectionType} · {Adapter.Name}";
    }

    private readonly NetworkAdapterService adapters;

    private readonly WindowsPrinterService windows;

    private readonly PrinterDiscoveryService discovery;

    private readonly PrinterIdentityService identity;

    private readonly AppSettings settings;
    private readonly ILogger logger;
    private (string Reason, PrinterQueue? Queue, DeviceChoice? Device, bool Separate, bool Confirmed)? lastValidation;

    private readonly CancellationTokenSource cancellation;

    private readonly ComboBox wifi = new() { MinHeight = 30 };
    private readonly ComboBox queues = new() { MinHeight = 30 };
    private readonly ComboBox devices = new() { MinHeight = 30 };
    private readonly RadioButton separate = new() { IsChecked = true };
    private readonly CheckBox compatible = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar progress = new() { Height = 4, Visibility = Visibility.Hidden };
    private readonly Button find;
    private readonly Button connect;
    private readonly CancellationTokenRegistration parentRegistration;
    private bool busy;
    private bool closing;

    public SetupSelection? Selection { get; private set; }
    public Guid? AdapterOverride { get; private set; }

    public SetupWindow(NetworkAdapterService adapters, WindowsPrinterService windows, PrinterDiscoveryService discovery,
        PrinterIdentityService identity, AppSettings settings, CancellationToken parent, ILogger logger)
        : base(F("PrintBridge Setup {0}", Program.Version), T("Connect a printer"), 715, 610)
    {
        this.adapters = adapters;
        this.windows = windows;
        this.discovery = discovery;
        this.identity = identity;
        this.settings = settings;
        this.logger = logger;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
        parentRegistration = parent.Register(() => Dispatcher.InvokeAsync(() =>
        {
            if (IsVisible)
                Close();
        }));

        AddField(T("Network adapter"), wifi);
        AddField(T("Print queue"), queues);
        AddField(T("Physical device"), devices);

        var existing = new RadioButton();
        AddOption(separate, T("Create a separate queue (recommended)"));
        AddOption(existing, T("Switch the selected queue and save its original port"));
        AddOption(compatible, T("I verified the printer and its driver's RAW TCP/9100 support."));
        status.Margin = new Thickness(0, 10, 0, 8);
        Body.Children.Add(status);
        Body.Children.Add(progress);

        find = AddAction(T("Find devices"));
        connect = AddAction(T("Connect"), primary: true);
        connect.IsEnabled = false;
        var cancel = AddAction(T("Cancel"));
        cancel.IsCancel = true;

        wifi.SelectionChanged += (_, _) =>
        {
            devices.Items.Clear();
            if (!busy)
                status.Text = T("Search using the selected adapter.");
            ValidateSelection();
        };
        queues.SelectionChanged += (_, _) => Suggest();
        devices.SelectionChanged += (_, _) =>
        {
            compatible.IsChecked = false;
            ValidateSelection();
        };
        separate.Checked += (_, _) => ValidateSelection();
        separate.Unchecked += (_, _) => ValidateSelection();
        compatible.Checked += (_, _) => ValidateSelection();
        compatible.Unchecked += (_, _) => ValidateSelection();
        find.Click += async (_, _) => await FindAsync();
        connect.Click += (_, _) =>
        {
            if (wifi.SelectedItem is AdapterChoice choice && queues.SelectedItem is PrinterQueue queue &&
                devices.SelectedItem is DeviceChoice device && connect.IsEnabled)
            {
                Selection = new SetupSelection(device.Adapter, queue, device.Printer, separate.IsChecked == true);
                AdapterOverride = choice.Id;
                DialogResult = true;
            }
        };
        Loaded += async (_, _) => await FindAsync();
    }

    private void AddField(string title, ComboBox field)
    {
        var label = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        System.Windows.Automation.AutomationProperties.SetLabeledBy(field, label);
        field.Margin = new Thickness(0, 0, 0, 12);
        Body.Children.Add(label);
        Body.Children.Add(field);
    }

    private void AddOption(System.Windows.Controls.Primitives.ToggleButton option, string title)
    {
        option.Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap };
        option.Margin = new Thickness(0, 0, 0, 8);
        Body.Children.Add(option);
    }

    private void Suggest()
    {
        compatible.IsChecked = false;
        if (queues.SelectedItem is PrinterQueue queue)
        {
            var choices = devices.Items.Cast<DeviceChoice>();
            NetworkPrinter? suggestion = identity.Suggest(queue, choices.Select(choice => choice.Printer));
            if (suggestion != null)
            {
                devices.SelectedItem = choices.First(choice => choice.Printer == suggestion);
            }
        }
        ValidateSelection();
    }

    private void ValidateSelection()
    {
        if (busy)
            return;

        var queue = queues.SelectedItem as PrinterQueue;
        var choice = devices.SelectedItem as DeviceChoice;
        var device = choice?.Printer;
        bool createSeparate = separate.IsChecked == true;
        bool confirmed = compatible.IsChecked == true;
        bool? trackable = device == null ? null : PrinterIdentity.Confirm(device).IsTrackable;
        string reason;
        if (device == null)
        {
            reason = "PrinterNotSelected";
        }
        else if (!device.Raw)
        {
            reason = "Tcp9100Unavailable";
            status.Text = F("TCP/9100 is unavailable. Other protocols detected: IPP {0}, LPR {1}. {2}.", YesNo(device.Ipp), YesNo(device.Lpr), device.Source);
        }
        else if (trackable == false)
        {
            reason = "PrinterIdentityMissing";
            status.Text = T("The device did not provide a MAC address, UUID, or serial number. Automatic tracking is unavailable.");
        }
        else if (queue == null)
        {
            reason = "QueueNotSelected";
        }
        else if (!WindowsPrinterIntegrationService.Eligible(queue))
        {
            reason = "QueueIneligible";
            status.Text = T("RAW printing is unavailable for the selected queue or mode.");
        }
        else if (!createSeparate && queue.Shared)
        {
            reason = "SharedQueueCannotBeSwitched";
            status.Text = T("RAW printing is unavailable for the selected queue or mode.");
        }
        else
        {
            reason = confirmed ? "Ready" : "CompatibilityNotConfirmed";
            status.Text = T("Verify the printer's model, address, identifier, and driver compatibility.");
        }
        connect.IsEnabled = reason == "Ready";

        var validation = (reason, queue, choice, createSeparate, confirmed);
        if (lastValidation == validation)
            return;

        lastValidation = validation;
        logger.LogInformation(
            "Printer setup validation: {SetupReason}; Connect enabled: {ConnectEnabled}; queue: {Queue}; driver: {Driver}; port: {QueuePort}; queue RAW: {QueueRaw}; shared: {QueueShared}; printer: {PrinterModel}; address: {PrinterAddress}; adapter: {Adapter}; detected RAW: {PrinterRaw}; identity verified: {PrinterTrackable}; separate queue: {SeparateQueue}; confirmed: {CompatibilityConfirmed}",
            reason, connect.IsEnabled, queue?.Name, queue?.Driver, queue?.Port, queue?.RawPort, queue?.Shared,
            device?.Model, device?.Address.ToString(), choice?.Adapter.Name, device?.Raw, trackable, createSeparate, confirmed);
    }

    private static string YesNo(bool value)
    {
        if (!value)
        {
            return T("none");
        }
        return T("yes");
    }

    private async Task FindAsync()
    {
        if (busy || closing)
        {
            return;
        }
        busy = true;
        lastValidation = null;
        find.IsEnabled = wifi.IsEnabled = queues.IsEnabled = devices.IsEnabled = connect.IsEnabled = false;
        progress.Visibility = Visibility.Visible;
        progress.IsIndeterminate = true;
        status.Text = T("Finding network adapters, queues, and printers…");
        try
        {
            CancellationToken token = cancellation.Token;
            Guid? selectedAdapter = wifi.SelectedItem is AdapterChoice choice ? choice.Id : settings.Adapter;
            var interfaces = await Task.Run(adapters.GetAdapters, token);
            wifi.Items.Clear();
            wifi.Items.Add(new AdapterChoice(null, T("Automatic")));
            foreach (var item in interfaces.DistinctBy(item => item.Id))
                wifi.Items.Add(new AdapterChoice(item.Id, item.ToString()));
            if (selectedAdapter is Guid saved && !interfaces.Any(item => item.Id == saved))
                wifi.Items.Add(new AdapterChoice(saved, T("Saved adapter unavailable")));
            wifi.SelectedItem = wifi.Items.Cast<AdapterChoice>().First(item => item.Id == selectedAdapter);
            SpoolerState spool = await Task.Run((Func<SpoolerState>)windows.Read, token);
            string? previous = (queues.SelectedItem as PrinterQueue)?.Name;
            PrinterQueue[] available = spool.Queues.Where((PrinterQueue queue) => queue.Local && !queue.Port.StartsWith("PrintBridge_", StringComparison.OrdinalIgnoreCase)).ToArray();
            queues.Items.Clear();
            foreach (var queue in available)
                queues.Items.Add(queue);
            queues.SelectedItem = available.FirstOrDefault((PrinterQueue q) => q.Name == previous) ?? ((available.Length == 1) ? available[0] : null);
            var selected = LanAdapter.OrderForPrinter(interfaces, selectedAdapter, settings.Profile?.LastAdapter, null);
            logger.LogInformation("Printer setup discovery: {QueueCount} available Windows queues, {AdapterCount} adapters to search",
                available.Length, selected.Length);
            if (selected.Length == 0)
            {
                status.Text = T("Connect to the selected local network and try again.");
                return;
            }
            devices.Items.Clear();
            status.Text = T("Searching the local network…");
            var hints = available.Select(q => q.Host).OfType<string>().ToArray();
            var paths = new List<DeviceChoice>();
            foreach (var adapter in selected)
            {
                foreach (var printer in await discovery.FindAsync(adapter, hints, scan: true, token))
                    paths.Add(new DeviceChoice(adapter, printer));
            }
            foreach (var path in paths)
                devices.Items.Add(path);
            var found = paths.Select(path => path.Printer).ToArray();
            logger.LogInformation("Printer setup discovery found {PrinterCount} candidates", found.Length);
            if (queues.SelectedIndex == -1)
            {
                PrinterQueue[] matching = available.Where((PrinterQueue q) => identity.Suggest(q, found) != null).Take(2).ToArray();
                if (matching.Length == 1)
                {
                    queues.SelectedItem = matching[0];
                }
            }
            Suggest();
            if (devices.SelectedIndex == -1)
            {
                status.Text = ((found.Length == 0) ? T("No devices found. Check that the printer is on and reachable from this network.") : F("Found {0} printer connections. Select a queue and verify the physical printer.", found.Length));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Printer setup discovery failed");
            status.Text = error.Message;
        }
        finally
        {
            busy = false;
            if (closing)
            {
                Close();
            }
            else
            {
                progress.IsIndeterminate = false;
                progress.Visibility = Visibility.Hidden;
                find.IsEnabled = wifi.IsEnabled = queues.IsEnabled = devices.IsEnabled = true;
                ValidateSelection();
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (busy)
        {
            closing = true;
            cancellation.Cancel();
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        parentRegistration.Dispose();
        cancellation.Dispose();
        base.OnClosed(e);
    }
}
