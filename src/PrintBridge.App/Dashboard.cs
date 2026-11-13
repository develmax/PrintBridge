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

    public bool Busy => operation != null;

    public AppSettings Settings { get; private set; }

    public PrintPlan? Pending { get; private set; }

    public PrinterMonitorService Monitor { get; }

    public TcpBridgeService Bridge { get; }
    public Color StatusColor => Monitor.Paused || Settings.Profile == null ? Color.Gray
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
        bool ready = state.Available && !state.Searching && Bridge.Running && Bridge.Target != null && Pending == null;
        string? model = state.Printer?.Model ?? Settings.Profile?.Identity.Model;
        view.PrinterName.Text = model ?? T("Printer");
        view.QueueName.Text = plan?.Queue ?? T("No queue selected");
        view.PrinterLink.IsEnabled = view.QueueLink.IsEnabled = plan != null;
        view.StatusIcon.Text = ready ? "✓" : state.Searching ? "…" : plan == null ? "•" : "×";
        view.ConnectionStatus.Text = ready ? T("Ready to print")
            : state.Searching ? T("Checking connection…")
            : plan == null ? T("No queue selected") : T("Printing unavailable");
        var statusColor = ready ? "#215F42" : state.Searching || plan == null ? "#365776" : "#84283C";
        view.ConnectionStatus.Foreground = view.StatusIcon.Foreground = BrushFromHex(statusColor);
        view.StatusPill.Background = BrushFromHex(ready ? "#D8F3E2"
            : state.Searching || plan == null ? "#DCE8F6" : "#FBE1E5");
        view.FailureReason.Text = Pending != null ? T("Setup did not finish. Restore the queue to continue.")
            : state.Message;
        view.FailureReason.Visibility = !ready && !state.Searching
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

    private void OpenPrintDialog(string command)
    {
        var plan = Settings.Profile?.Print ?? Pending;
        if (plan == null)
            return;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "rundll32.exe"))
            {
                UseShellExecute = false,
                ArgumentList = { "printui.dll,PrintUIEntry", command, "/n", plan.Queue }
            });
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            logger.LogWarning(error, "Could not open the Windows print dialog");
            MessageBox.Show(this, T("Could not open the Windows print dialog."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task RunAsync(Func<Task> work, bool background = false, bool showProgress = true)
    {
        if (Busy || exiting)
        {
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
        }
        catch (Exception ex2)
        {
            logger.LogError(ex2, T("Operation failed"));
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
            var wizard = new SetupWindow(adapters, windows, discovery, identities, Settings, stop.Token);
            if (wizard.ShowOwned(Handle) == true)
            {
                SetupSelection? selection = wizard.Selection;
                if (selection != null)
                {
                    if (Settings.Profile != null)
                    {
                        if (MessageBox.Show(this, T("Restore the current queue and connect the selected printer?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        {
                            return;
                        }
                        await RestoreCoreAsync();
                    }
                    var (adapter, source, device, separate) = selection;
                    PrinterIdentity identity = PrinterIdentity.Confirm(device);
                    await Monitor.ConfirmPrinterAsync(adapter, device, identity, Settings.ConnectSeconds, stop.Token);
                    SpoolerState spooler = await Task.Run((Func<SpoolerState>)windows.Read, stop.Token);
                    PrintPlan? plan = PrintPlan.Create(source, Settings.LocalPort, separate, spooler);
                    Monitor.Paused = false;
                    Monitor.EnsureListener(Settings, plan.LocalPort);
                    storage.SavePending(plan);
                    Pending = plan;
                    logger.LogInformation("User confirmed device {Address} and queue {Queue}", device.Address, source.Name);
                    await install.ChangeQueueAsync("connect", plan);
                    Save(Settings with
                    {
                        Profile = new Profile(plan, identity, adapter.Id),
                        Adapter = wizard.AdapterOverride
                    });
                    storage.ClearPending();
                    Pending = null;
                    await RefreshAsync(force: true);
                    if (Monitor.State.Available && !exiting && MessageBox.Show(this, T("Queue connected. Print a test page?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        await SubmitTestPageAsync(plan.Queue);
                    }
                }
            }
        });
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
        return RunAsync(async () =>
        {
            await Bridge.DisposeAsync();
            Monitor.Paused = false;
            await RefreshAsync(force: false);
        });
    }

    public Task ToggleAsync()
    {
        return RunAsync(async () =>
        {
            Monitor.Paused = !Monitor.Paused;
            if (Monitor.Paused)
            {
                await Bridge.DisposeAsync();
                logger.LogInformation("User stopped the bridge");
            }
            await RefreshAsync(force: false);
        });
    }

    private Task TestPageAsync()
    {
        return RunAsync(async () =>
        {
            Profile? profile = Settings.Profile;
            if (profile == null || !Bridge.Running || Bridge.Target == null || Pending != null)
            {
                throw new InvalidOperationException(T("Check the queue and bridge configuration first."));
            }
            await SubmitTestPageAsync(profile.Print.Queue);
        });
    }

    private async Task SubmitTestPageAsync(string queue)
    {
        await Task.Run(() =>
        {
            windows.TestPage(queue);
        }, stop.Token);
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
