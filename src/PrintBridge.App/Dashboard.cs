using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PrintBridge;

internal sealed class Dashboard : Form
{
    private readonly SettingsService storage;

    private readonly StartupService startup;

    private readonly NetworkAdapterService adapters;

    private readonly PrinterDiscoveryService discovery;

    private readonly PrinterIdentityService identities;

    private readonly WindowsPrinterService windows;

    private readonly InstallationIntegrationService install;

    private readonly DiagnosticsService diagnostics;

    private readonly ILogger<Dashboard> logger;

    private readonly CancellationTokenSource stop = new CancellationTokenSource();

    private readonly DashboardView view = new();
    private readonly Icon appIcon;

    private readonly System.Windows.Controls.MenuItem restore;
    private readonly System.Windows.Controls.MenuItem choosePrinter;

    private bool exiting;

    private bool networkInvalidated;

    private Task? operation;
    private bool connecting;
    private Microsoft.AspNetCore.Builder.WebApplication? experimentalIpp;
    public bool ExperimentalIppRunning => experimentalIpp != null;

    public bool Busy => operation != null;

    public AppSettings Settings { get; private set; }

    public PrintPlan? Pending { get; private set; }

    public PrinterMonitorService Monitor { get; }

    public TcpBridgeService Bridge { get; }
    public bool Connecting => connecting;
    public Color StatusColor => Connecting ? Color.Goldenrod
        : Monitor.Paused || Settings.Profile == null ? Color.Gray
        : Monitor.State.Searching ? Color.Goldenrod
        : Monitor.State.Available && Bridge.LastError == null ? Color.ForestGreen
        : Color.Firebrick;

    public event Action? StatusChanged;

    public event Action? ExitRequested;
    public event Func<IWin32Window, Task>? CheckUpdatesRequested;
    public event Action? SettingsClosed;

    public Dashboard(SettingsService storage, StartupService startup, NetworkAdapterService adapters, PrinterDiscoveryService discovery, PrinterIdentityService identities, WindowsPrinterService windows, InstallationIntegrationService install, DiagnosticsService diagnostics, PrinterMonitorService monitor, TcpBridgeService bridge, ILogger<Dashboard> logger)
    {
        this.storage = storage;
        this.startup = startup;
        this.adapters = adapters;
        this.discovery = discovery;
        this.identities = identities;
        this.windows = windows;
        this.install = install;
        this.diagnostics = diagnostics;
        this.logger = logger;
        Monitor = monitor;
        Bridge = bridge;
        Settings = storage.Load();
        Pending = storage.Pending();
        if (Pending != null && Pending == Settings.Profile?.Print)
        {
            storage.ClearPending();
            Pending = null;
        }
        diagnostics.Verbose = Settings.Verbose;
        Text = "PrintBridge " + Program.Version;
        using (var resource = typeof(Dashboard).Assembly.GetManifestResourceStream("PrintBridge.AppIcon")!)
            appIcon = new Icon(resource, 64, 64);
        Icon = appIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 660);
        MinimumSize = new Size(690, 560);
        MaximizeBox = false;
        var host = new System.Windows.Forms.Integration.ElementHost { Dock = DockStyle.Fill, Child = view };
        Controls.Add(host);
        view.Language = System.Windows.Markup.XmlLanguage.GetLanguage(Normalize(Settings.Language));
        if (System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft)
            view.FlowDirection = System.Windows.FlowDirection.RightToLeft;
        view.Configure.Content = T("Connect");
        view.Check.Content = T("Check connection");
        view.TestPage.Content = T("Print test page");
        view.Preferences.Content = T("Settings");
        view.More.Content = T("More");
        view.QueueLabel.Text = T("Print queue");
        view.NetworkLabel.Text = T("Local network");
        view.Bridge.Header = T("Printer connection");
        view.Routing.Header = T("Routing");
        view.Configure.Click += async (_, _) => await ConfigureAsync();
        view.Check.Click += async (_, _) => await CheckAsync(force: false);
        view.TestPage.Click += async (_, _) => await TestPageAsync();
        view.Preferences.Click += async (_, _) => await SettingsAsync();
        view.More.Click += (_, _) =>
        {
            view.MoreMenu.PlacementTarget = view.More;
            view.MoreMenu.FlowDirection = view.FlowDirection;
            view.MoreMenu.IsOpen = true;
        };
        view.QueueLink.Click += (_, _) => OpenPrintDialog("/o");
        view.PrinterLink.Click += (_, _) => OpenPrintDialog("/p");

        choosePrinter = AddMenu(T("Choose another printer"), ConfigureAsync, "\uE749");
        AddMenu(T("Find printer"), () => CheckAsync(force: true), "\uE721");
        AddMenu(T("Restart bridge"), RestartAsync, "\uE72C");
        restore = AddMenu(T("Restore original port"), RestoreAsync, "\uE7A7");
        view.MoreMenu.Items.Add(new System.Windows.Controls.Separator { Background = BrushFromHex("#CEDBEC") });
        AddMenu(T("Diagnostics"), ExportAsync, "\uE9D9");
        AddMenu(T("Report a bug"), ReportBugAsync, "\uE939");
        AddMenu(T("Open logs"), () =>
        {
            OpenLogs();
            return Task.CompletedTask;
        }, "\uE8B7");
        AddMenu(T("About"), () => { new AboutWindow().ShowOwned(Handle); return Task.CompletedTask; }, "\uE946");
        Monitor.Changed += Render;
        Render();
    }

    private System.Windows.Controls.MenuItem AddMenu(string title, Func<Task> click, string glyph)
    {
        var item = new System.Windows.Controls.MenuItem
        {
            Header = title,
            Style = (System.Windows.Style)view.FindResource("MoreMenuItem"),
            Icon = new System.Windows.Controls.TextBlock
            {
                Text = glyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                Foreground = BrushFromHex("#365776"),
                FlowDirection = System.Windows.FlowDirection.LeftToRight
            }
        };
        item.Click += async (_, _) => await click();
        view.MoreMenu.Items.Add(item);
        return item;
    }

    public void Render()
    {
        MonitorState state = Monitor.State;
        PrintPlan? plan = Settings.Profile?.Print ?? Pending;
        bool checking = connecting || state.Searching;
        bool ready = state.Available && !checking && Bridge.Running && Bridge.Target != null && Pending == null;
        string? model = state.Printer?.Model ?? Settings.Profile?.Identity.Model;
        view.PrinterName.Text = model ?? T("Printer");
        view.QueueName.Text = plan?.Queue ?? T("No queue selected");
        view.PrinterLink.IsEnabled = view.QueueLink.IsEnabled = plan != null;
        view.StatusIcon.Text = ready ? "✓" : checking ? "…" : plan == null ? "•" : "×";
        view.ConnectionStatus.Text = ready ? T("Ready to print")
            : connecting ? T("Connecting to printer")
            : checking ? T("Checking connection…")
            : plan == null ? T("No queue selected") : T("Printing unavailable");
        var statusColor = ready ? "#215F42" : checking || plan == null ? "#365776" : "#84283C";
        view.ConnectionStatus.Foreground = view.StatusIcon.Foreground = BrushFromHex(statusColor);
        view.StatusPill.Background = BrushFromHex(ready ? "#D8F3E2"
            : checking || plan == null ? "#DCE8F6" : "#FBE1E5");
        view.FailureReason.Text = Pending != null ? T("Setup did not finish. Restore the queue to continue.")
            : state.Message;
        view.FailureReason.Visibility = !ready && !checking
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
        view.QueueJobs.Text = state.Queue is { JobCount: int count } && SpoolerState.Same(state.Queue.Name, plan?.Queue)
            ? F("Jobs in queue: {0}", count) : "";
        view.QueueJobs.Visibility = VisibilityFor(view.QueueJobs.Text.Length != 0);
        view.QueueDetails.Text = plan?.Driver ?? "";
        view.QueueDetails.Visibility = VisibilityFor(plan != null);
        LanAdapter? adapter = state.Adapter;
        view.NetworkSummary.Text = adapter?.ToString() ?? T("No active local network adapter verified");
        string bridgeStatus = $"{(Bridge.Running ? T("Running") : T("Stopped"))} · 127.0.0.1:{plan?.LocalPort ?? Settings.LocalPort} → {Bridge.Target?.Printer.Address.ToString() ?? T("waiting")}:9100 · {T("connections")} {Bridge.Active}";
        if (Bridge.LastError is { } error)
            bridgeStatus += $"\r\n{T("Last connection error")}: {error}";
        if (plan != null)
            bridgeStatus += $"\r\n{T("Current port")}: {state.Queue?.Port ?? T("pending verification")} · {T("original")}: {plan.OriginalPort}";
        if (state.Printer is { } printer)
            bridgeStatus += $"\r\nMAC: {printer.Mac ?? T("unavailable")} · {T("last seen")}: {Monitor.LastSuccess?.ToString("g") ?? T("unavailable")}";
        if (view.BridgeSummary.Text != bridgeStatus)
            view.BridgeSummary.Text = bridgeStatus;
        string route = state.Route ?? T("Route not checked yet");
        if (view.RouteSummary.Text != route)
            view.RouteSummary.Text = route;
        view.Configure.Visibility = VisibilityFor(Settings.Profile == null && Pending == null);
        view.Check.Visibility = view.TestPage.Visibility = VisibilityFor(Settings.Profile != null && Pending == null);
        choosePrinter.Visibility = VisibilityFor(Settings.Profile != null && Pending == null);
        view.TestPage.IsEnabled = !Busy && Bridge.Running && Bridge.Target != null && Pending == null;
        restore.IsEnabled = !Busy && plan != null;
        StatusChanged?.Invoke();
    }

    private static System.Windows.Visibility VisibilityFor(bool visible) =>
        visible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    private static System.Windows.Media.Brush BrushFromHex(string value)
    {
        var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(value)!;
        brush.Freeze();
        return brush;
    }

    private void OpenPrintDialog(string command, bool interactive = true)
    {
        var plan = Settings.Profile?.Print ?? Pending;
        if (plan == null)
        {
            if (!interactive)
                throw new InvalidOperationException(T("No queue selected"));
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "rundll32.exe"))
            {
                UseShellExecute = false,
                ArgumentList = { "printui.dll,PrintUIEntry", command, "/n", plan.Queue }
            });
        }
        catch (System.ComponentModel.Win32Exception error) when (interactive)
        {
            logger.LogWarning(error, "Could not open the Windows print dialog");
            MessageBox.Show(this, T("Could not open the Windows print dialog."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task RunAsync(Func<Task> work, bool background = false, bool showProgress = true, bool command = false)
    {
        if (Busy || exiting)
        {
            if (command)
                throw new InvalidOperationException("PrintBridge is busy. Close its dialogs or wait for the current operation, then retry.");
            return;
        }
        view.IsEnabled = false;
        if (showProgress)
        {
            view.CheckProgress.Visibility = System.Windows.Visibility.Visible;
            view.CheckProgress.IsIndeterminate = true;
        }
        TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operation = completion.Task;
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (command)
                throw;
        }
        catch (Exception ex2)
        {
            logger.LogError(ex2, T("Operation failed"));
            if (command)
                throw;
            if (!background && !exiting)
            {
                MessageBox.Show(this, ex2.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
        }
        finally
        {
            operation = null;
            completion.SetResult();
            view.CheckProgress.IsIndeterminate = false;
            view.CheckProgress.Visibility = System.Windows.Visibility.Hidden;
            view.IsEnabled = true;
            Render();
        }
    }

    private async Task RefreshAsync(bool force)
    {
        networkInvalidated = false;
        await Monitor.RefreshAsync(Settings, force, stop.Token);
        Settings = storage.Load();
        if (networkInvalidated)
        {
            Bridge.SetTarget(null);
        }
    }

    public void InvalidateNetwork()
    {
        networkInvalidated = true;
        Bridge.SetTarget(null);
    }

    public Task CheckAsync(bool force, bool background = false)
    {
        if (Settings.Profile == null && !background && Pending == null)
        {
            return ConfigureAsync();
        }
        return RunAsync(() => RefreshAsync(force), background);
    }

    public Task ConfigureAsync()
    {
        return RunAsync(async () =>
        {
            if (Pending != null)
            {
                throw new InvalidOperationException(T("Restore the queue from the incomplete setup first."));
            }
            var wizard = new SetupWindow(adapters, windows, discovery, identities, Settings, stop.Token,
                diagnostics.CreateLogger(typeof(SetupWindow).FullName!));
            if (wizard.ShowOwned(Handle) == true)
            {
                SetupSelection? selection = wizard.Selection;
                if (selection != null)
                {
                    bool replace = Settings.Profile != null;
                    if (replace)
                    {
                        if (MessageBox.Show(this, T("Restore the current queue and connect the selected printer?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        {
                            return;
                        }
                    }
                    var plan = await ConnectCoreAsync(selection, wizard.AdapterOverride, replace);
                    if (Monitor.State.Available && !exiting && MessageBox.Show(this, T("Queue connected. Print a test page?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        await SubmitTestPageAsync(plan.Queue);
                    }
                }
            }
        });
    }

    private async Task<PrintPlan> ConnectCoreAsync(SetupSelection selection, Guid? adapterOverride, bool replace = false)
    {
        connecting = true;
        Render();
        try
        {
            var (adapter, source, device, separate) = selection;
            var identity = PrinterIdentity.Confirm(device);
            await Monitor.ConfirmPrinterAsync(adapter, device, identity, Settings.ConnectSeconds, stop.Token);
            if (replace)
                await RestoreCoreAsync();
            var spooler = await Task.Run(windows.Read, stop.Token);
            var currentSource = spooler.Queue(source.Name);
            if (currentSource == null || !WindowsPrinterIntegrationService.Eligible(currentSource) ||
                !SpoolerState.Same(currentSource.Port, source.Port) || !SpoolerState.Same(currentSource.Driver, source.Driver))
                throw new InvalidOperationException(T("The original queue changed or its driver is incompatible with RAW printing."));
            var plan = PrintPlan.Create(currentSource, Settings.LocalPort, separate, spooler);
            Monitor.Paused = false;
            Monitor.EnsureListener(Settings, plan.LocalPort);
            storage.SavePending(plan);
            Pending = plan;
            logger.LogInformation("User confirmed device {Address} and queue {Queue}", device.Address, source.Name);
            await install.ChangeQueueAsync("connect", plan);
            Save(Settings with { Profile = new Profile(plan, identity, adapter.Id), Adapter = adapterOverride });
            storage.ClearPending();
            Pending = null;
            await RefreshAsync(force: true);
            // The printer can sleep while Windows changes its port. Keep setup active
            // while retrying reachability, without repeating the queue transaction.
            for (int attempt = 1; Monitor.State.RetryConnection && attempt < 6; attempt++)
            {
                logger.LogInformation("Waiting for printer after queue setup, connection check {Attempt}", attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);
                await RefreshAsync(force: false);
            }
            return plan;
        }
        finally
        {
            connecting = false;
            Render();
        }
    }

    internal async Task<object> ExecuteCommandAsync(string[] args)
    {
        string command = args[0];
        if (args.Length == 1)
        {
            switch (command)
            {
                case "status": return CommandStatus();
                case "diagnostics": return CreateReport(includeIdentifiers: false);
                case "logs": return new { Directory = SettingsService.LogFolder };
            }
        }

        object? result = null;
        await RunAsync(async () =>
        {
            switch (command)
            {
                case "adapters" when args.Length == 1:
                    result = (await Task.Run(adapters.GetAdapters, stop.Token)).Select(item => new
                    {
                        item.Id, item.Name, item.ConnectionType, Address = item.Address.ToString(), item.Prefix
                    }).ToArray();
                    break;
                case "queues" when args.Length == 1:
                    result = (await Task.Run(windows.Read, stop.Token)).Queues.Select(queue => new
                    {
                        queue.Name, queue.Driver, queue.Port, queue.Host, queue.Status, queue.JobCount, queue.Shared,
                        Eligible = IsSetupQueue(queue) && WindowsPrinterIntegrationService.Eligible(queue)
                    }).ToArray();
                    break;
                case "printers" when args.Length <= 2:
                    var spooler = await Task.Run(windows.Read, stop.Token);
                    var hints = spooler.Queues.Where(IsSetupQueue).Select(queue => queue.Host).OfType<string>().ToArray();
                    var candidates = new List<object>();
                    foreach (var adapter in await CommandAdaptersAsync(args.Length == 2 ? args[1] : null))
                    {
                        foreach (var device in await discovery.FindAsync(adapter, hints, scan: true, stop.Token))
                            candidates.Add(new
                            {
                                Adapter = adapter.Id, Address = device.Address.ToString(), device.Model, device.Host,
                                device.Raw, device.Ipp, device.VerifiedIppUri, device.Lpr,
                                Trackable = PrinterIdentity.Confirm(device).IsTrackable
                            });
                    }
                    result = candidates;
                    break;
                case "connect" when args.Length >= 4:
                    await ConnectCommandAsync(args);
                    break;
                case "restore" or "disconnect" when args.Length == 1:
                    await RestoreCoreAsync();
                    break;
                case "check" when args.Length == 1:
                    await RefreshAsync(force: true);
                    break;
                case "open-queue" or "printer-properties" when args.Length == 1:
                    OpenPrintDialog(command == "open-queue" ? "/o" : "/p", interactive: false);
                    break;
                case "start" or "stop" or "restart" when args.Length == 1:
                    await SetBridgeAsync(command);
                    break;
                case "test-page" when args.Length == 1:
                    var queueName = ConfiguredQueue();
                    await SubmitTestPageAsync(queueName, notify: false);
                    result = new { Queued = true, Queue = queueName };
                    break;
                case "ipp-start" when args.Length == 3:
                    if (experimentalIpp != null)
                        throw new InvalidOperationException("Experimental IPP proxy is already running. Use ipp-stop first.");
                    if (!System.Net.IPAddress.TryParse(args[1], out var ippAddress) ||
                        ippAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                        !Guid.TryParse(args[2], out var ippAdapter))
                        throw new ArgumentException("Use ipp-start <printer IPv4> <adapter GUID>.");
                    var lan = (await CommandAdaptersAsync(ippAdapter.ToString())).Single();
                    var found = await discovery.CheckAddressAsync(lan, ippAddress, stop.Token);
                    if (found?.VerifiedIppUri is not { } endpoint || !PrinterIdentity.Confirm(found).IsTrackable)
                        throw new InvalidOperationException("No verified IPP printer found at this address.");
                    var target = new BridgeTarget(lan, found, PrinterIdentity.Confirm(found));
                    experimentalIpp = await IppProxy.StartAsync(0, () => target, async (destination, token) =>
                    {
                        var observed = await discovery.CheckAddressAsync(destination.Adapter, destination.Printer.Address, token);
                        if (observed == null || !identities.Matches(destination.Identity, observed))
                            throw new IOException("IPP printer identity could not be verified.");
                        var socket = await RoutingDiagnosticsService.ConnectAsync(destination.Adapter,
                            destination.Printer.Address, endpoint.Port, TimeSpan.FromSeconds(Settings.ConnectSeconds), token);
                        return socket.GetStream();
                    }, logger, stop.Token);
                    var local = new Uri(experimentalIpp.Urls.Single());
                    result = new { Experimental = true, Uri = new UriBuilder("ipp", "127.0.0.1", local.Port)
                        { Path = endpoint.AbsolutePath }.Uri.AbsoluteUri,
                        QueueConfigured = false, Lifetime = "Until ipp-stop or PrintBridge exits" };
                    break;
                case "ipp-stop" when args.Length == 1:
                    if (experimentalIpp != null)
                    {
                        await experimentalIpp.StopAsync(stop.Token);
                        await experimentalIpp.DisposeAsync();
                        experimentalIpp = null;
                    }
                    result = new { Stopped = true };
                    break;
                default:
                    throw new ArgumentException("Invalid command or arguments. Run PrintBridge.Cli help.");
            }
        }, showProgress: false, command: true);
        return result ?? CommandStatus();
    }

    private object CommandStatus() => new
    {
        Version = Program.Version, Busy, Connecting = connecting, Monitor.Paused, Bridge.Running,
        Available = !connecting && Monitor.State.Available, Monitor.State.Searching,
        Message = connecting ? T("Connecting to printer") : Monitor.State.Message,
        Queue = Settings.Profile?.Print.Queue, PrinterAddress = Monitor.State.Printer?.Address.ToString() ?? Settings.Profile?.Identity.LastAddress,
        JobCount = Monitor.State.Queue?.JobCount, RecoveryRequired = Pending != null && !connecting
    };

    internal static bool IsSetupQueue(PrinterQueue queue) =>
        queue.Local && !queue.Port.StartsWith("PrintBridge_", StringComparison.OrdinalIgnoreCase);

    private async Task<LanAdapter[]> CommandAdaptersAsync(string? adapterId)
    {
        Guid? selected = adapterId == null ? null : Guid.Parse(adapterId);
        var available = await Task.Run(adapters.GetAdapters, stop.Token);
        var candidates = LanAdapter.OrderForPrinter(available, selected, Settings.Profile?.LastAdapter, null);
        return candidates.Length > 0 ? candidates : throw new InvalidOperationException("No matching LAN adapter is available.");
    }

    private async Task ConnectCommandAsync(string[] args)
    {
        if (args[3] is not ("separate" or "switch") ||
            !System.Net.IPAddress.TryParse(args[2], out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Use connect <queue> <IPv4> <separate|switch> [--adapter <GUID>] [--replace].");

        string? adapterId = null;
        bool replace = false;
        for (int index = 4; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--confirm-raw":
                    break;
                case "--replace":
                    replace = true;
                    break;
                case "--adapter" when index + 1 < args.Length && adapterId == null:
                    adapterId = args[++index];
                    break;
                default:
                    throw new ArgumentException($"Unknown connect option: {args[index]}.");
            }
        }

        if (Pending != null)
            throw new InvalidOperationException(T("Restore the queue from the incomplete setup first."));
        if (Settings.Profile != null && !replace)
            throw new InvalidOperationException("A printer is already connected. Use --replace to restore its queue and connect another printer.");

        var spooler = await Task.Run(windows.Read, stop.Token);
        var source = spooler.Queue(args[1]);
        var original = Settings.Profile?.Print;
        if (replace && original is { Separate: false } && source != null && SpoolerState.Same(source.Name, original.Queue))
            source = source with { Port = original.OriginalPort, RawPort = null };
        bool separate = args[3] == "separate";
        if (source == null || !IsSetupQueue(source) || (!separate && source.Shared))
            throw new InvalidOperationException(T("RAW printing is unavailable for the selected queue or mode."));
        if (!WindowsPrinterIntegrationService.Eligible(source))
            throw new InvalidOperationException(WindowsPrinterIntegrationService.IneligibleQueueMessage(source));

        foreach (var adapter in await CommandAdaptersAsync(adapterId))
        {
            var device = await discovery.CheckAddressAsync(adapter, address, stop.Token);
            if (device is { Raw: true } && PrinterIdentity.Confirm(device).IsTrackable)
            {
                await ConnectCoreAsync(new SetupSelection(adapter, source, device, separate), adapterId == null ? null : adapter.Id, replace);
                return;
            }
        }
        throw new InvalidOperationException("The printer did not respond on TCP 9100 with a verifiable identity through the selected LAN adapter(s).");
    }

    private string ConfiguredQueue()
    {
        if (Settings.Profile is not { } profile || !Bridge.Running || Bridge.Target == null || Pending != null)
            throw new InvalidOperationException(T("Check the queue and bridge configuration first."));
        return profile.Print.Queue;
    }

    private async Task SetBridgeAsync(string command)
    {
        Monitor.Paused = command == "stop";
        if (command != "start")
            await Bridge.DisposeAsync();
        if (Monitor.Paused)
            logger.LogInformation("User stopped the bridge");
        await RefreshAsync(force: false);
    }

    private Task RestoreAsync()
    {
        return RunAsync(async () =>
        {
            PrintPlan? plan = Pending ?? Settings.Profile?.Print;
            if (plan != null)
            {
                string message = (plan.Separate ? T("Remove the PrintBridge queue and its port?") : T("Restore the selected queue's original port?"));
                if (MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    await RestoreCoreAsync();
                }
            }
        });
    }

    private async Task RestoreCoreAsync()
    {
        PrintPlan? plan = Pending ?? Settings.Profile?.Print;
        if (plan != null)
        {
            await install.ChangeQueueAsync("restore", plan);
            Save(Settings with
            {
                Profile = null
            });
            storage.ClearPending();
            Pending = null;
            await Bridge.DisposeAsync();
            await RefreshAsync(force: false);
        }
    }

    public Task RestartAsync()
    {
        return RunAsync(() => SetBridgeAsync("restart"));
    }

    public Task ToggleAsync()
    {
        return RunAsync(() => SetBridgeAsync(Monitor.Paused ? "start" : "stop"));
    }

    private Task TestPageAsync()
    {
        return RunAsync(() => SubmitTestPageAsync(ConfiguredQueue()));
    }

    private async Task SubmitTestPageAsync(string queue, bool notify = true)
    {
        await Task.Run(() =>
        {
            windows.TestPage(queue);
        }, stop.Token);
        if (notify)
            MessageBox.Show(this, T("Windows queued the test page. Check the printer for the result."), Text, MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
    }

    public Task SettingsAsync()
    {
        return RunAsync(async () =>
        {
            IReadOnlyList<LanAdapter> list = await Task.Run((Func<IReadOnlyList<LanAdapter>>)adapters.GetAdapters, stop.Token);
            var dialog = new PreferencesWindow(Settings, startup.Enabled, list, diagnostics.Clean,
                owner => CheckUpdatesRequested?.Invoke(owner) ?? Task.CompletedTask);
            new System.Windows.Interop.WindowInteropHelper(dialog).Owner = Handle;
            bool? result = dialog.ShowDialog();
            SettingsClosed?.Invoke();
            if (result == true)
            {
                AppSettings before = Settings;
                Save(dialog.Value);
                startup.Enabled = dialog.StartupEnabled;
                diagnostics.Verbose = Settings.Verbose;
                if (before.Language != Settings.Language &&
                    MessageBox.Show(this, T("Restart PrintBridge to apply the language change?"), Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    Program.RestartRequested = true;
                    BeginInvoke(() => ExitRequested?.Invoke());
                    return;
                }
                if (before.MaxConnections != Settings.MaxConnections || before.IdleSeconds != Settings.IdleSeconds || before.ConnectSeconds != Settings.ConnectSeconds)
                {
                    await Bridge.DisposeAsync();
                }
                await RefreshAsync(force: true);
            }
        }, showProgress: false);
    }

    private void Save(AppSettings value)
    {
        storage.Save(value);
        Settings = value;
    }

    public void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.LogFolder);
            Process.Start(new ProcessStartInfo(SettingsService.LogFolder)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
        }
    }

    private Task ExportAsync()
    {
        return RunAsync(async () =>
        {
            DialogResult response = MessageBox.Show(this, T("Remove IP and MAC addresses, network names, and queue names from the report?"), Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (response == DialogResult.Cancel)
            {
                return;
            }
            using SaveFileDialog save = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                FileName = "PrintBridge-diagnostics.json"
            };
            if (save.ShowDialog(this) == DialogResult.OK)
            {
                await DiagnosticsService.ExportAsync(save.FileName, CreateReport(includeIdentifiers: response == DialogResult.No));
            }
        });
    }

    private Task ReportBugAsync() => RunAsync(() =>
    {
        var dialog = new BugReportWindow(DiagnosticsService.FormatReport(CreateReport(includeIdentifiers: false)));
        dialog.ShowOwned(Handle);
        return Task.CompletedTask;
    });

    private Dictionary<string, object?> CreateReport(bool includeIdentifiers)
    {
        MonitorState state = Monitor.State;
        var report = new Dictionary<string, object?>
        {
            ["Version"] = typeof(Dashboard).Assembly.GetName().Version?.ToString(),
            ["Time"] = DateTimeOffset.Now,
            ["OS"] = Environment.OSVersion.ToString(),
            ["BridgeRunning"] = Bridge.Running,
            ["Connections"] = Bridge.Active,
            ["PrinterAvailable"] = state.Available,
            ["LastSuccess"] = Monitor.LastSuccess,
            ["NetworkAdapterPresent"] = state.Adapter != null,
            ["QueuePresent"] = state.Queue != null
        };
        if (includeIdentifiers)
        {
            report["Status"] = state.Message;
            report["Route"] = state.Route;
            report["NetworkAdapter"] = state.Adapter?.ToString();
            report["Queue"] = state.Queue;
            report["Printer"] = state.Printer?.ToString();
            report["Identity"] = Settings.Profile?.Identity;
            report["BridgeError"] = Bridge.LastError;
        }
        return report;
    }

    public async Task ShutdownAsync()
    {
        exiting = true;
        await stop.CancelAsync();
        if (operation != null)
        {
            await operation;
        }
        await Bridge.DisposeAsync();
        if (experimentalIpp != null)
            await experimentalIpp.DisposeAsync();
        stop.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Icon = null;
            appIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else if (!exiting)
        {
            e.Cancel = true;
            ExitRequested?.Invoke();
        }
        base.OnFormClosing(e);
    }
}
