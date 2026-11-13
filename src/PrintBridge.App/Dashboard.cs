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

    private readonly Label headline = new Label
    {
        AutoSize = true,
        MaximumSize = new Size(680, 0)
    };

    private readonly ToolTip portTip = new ToolTip();

    private readonly Label[] values = new Label[3];

    private readonly TextBox bridgeDetails = ScrollableDetails(T("TCP bridge details"));

    private readonly TextBox routeDetails = ScrollableDetails(T("Routing details"));

    private readonly ProgressBar progress = new ProgressBar
    {
        Dock = DockStyle.Top,
        Height = 4,
        Style = ProgressBarStyle.Marquee,
        Visible = false
    };

    private readonly FlowLayoutPanel actions = new FlowLayoutPanel
    {
        Dock = DockStyle.Bottom,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(18, 0, 18, 10)
    };

    private readonly Button configure;

    private readonly Button test;

    private readonly Button restore;

    private bool exiting;

    private bool networkInvalidated;

    private Task? operation;

    public bool Busy => operation != null;

    public AppSettings Settings { get; private set; }

    public PrintPlan? Pending { get; private set; }

    public PrinterMonitorService Monitor { get; }

    public TcpBridgeService Bridge { get; }

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
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 620);
        MinimumSize = new Size(690, 560);
        TableLayoutPanel body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(18, 18, 18, 6)
        };
        Controls.Add(body);
        Controls.Add(actions);
        Controls.Add(progress);
        body.Controls.Add(new Label
        {
            Text = "PrintBridge",
            Font = new Font(Font.FontFamily, 22f, FontStyle.Bold),
            AutoSize = true
        });
        body.Controls.Add(new Label
        {
            Text = T("Your printer. Always connected."),
            AutoSize = true
        });
        headline.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        headline.Margin = new Padding(3, 12, 3, 8);
        body.Controls.Add(headline);
        string[] sections = new string[3] { T("Windows queue"), T("Physical printer"), "Wi-Fi" };
        for (int index = 0; index < sections.Length; index++)
        {
            Label label = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(660, 0),
                Padding = new Padding(4, 0, 4, 5)
            };
            values[index] = label;
            GroupBox group = new GroupBox
            {
                Text = sections[index],
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(8)
            };
            label.Dock = DockStyle.Top;
            group.Controls.Add(label);
            body.Controls.Add(group);
        }
        for (int row = 0; row < 6; row++)
        {
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));
        body.Controls.Add(DetailsSection(T("TCP bridge"), bridgeDetails));
        body.Controls.Add(DetailsSection(T("Routing"), routeDetails));
        configure = Add(T("Connect Windows queue"), ConfigureAsync);
        Add(T("Find printer"), () => CheckAsync(force: true));
        Add(T("Check connection"), () => CheckAsync(force: false));
        Add(T("Restart bridge"), RestartAsync);
        test = Add(T("Print test page"), TestPageAsync);
        restore = Add(T("Restore original port"), RestoreAsync);
        Add(T("Settings"), SettingsAsync);
        Add(T("Diagnostics"), ExportAsync);
        Add(T("Report a bug"), ReportBugAsync);
        Add(T("Open logs"), () =>
        {
            OpenLogs();
            return Task.CompletedTask;
        });
        Add(T("About"), () => { using var about = new AboutForm(); about.ShowDialog(this); return Task.CompletedTask; });
        Monitor.Changed += Render;
        Render();
    }

    private static TextBox ScrollableDetails(string name)
    {
        return new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            TabStop = true,
            AccessibleName = name,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control
        };
    }

    private static GroupBox DetailsSection(string title, TextBox details)
    {
        return new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 60),
            Padding = new Padding(8),
            Controls = { (Control?)details }
        };
    }

    private Button Add(string title, Func<Task> click)
    {
        Button button = new Button
        {
            Text = title,
            AutoSize = true,
            Padding = new Padding(5, 3, 5, 3)
        };
        button.Click += async (object? _, EventArgs _) =>
        {
            await click();
        };
        actions.Controls.Add(button);
        return button;
    }

    public void Render()
    {
        MonitorState state = Monitor.State;
        PrintPlan? plan = Settings.Profile?.Print ?? Pending;
        headline.Text = ((Pending != null) ? T("Setup did not finish. Restore the queue to continue.") : state.Message);
        string? currentPort = state.Queue?.Port;
        bool usesBridge = plan is not null && SpoolerState.Same(currentPort, plan.PortName);
        string portLabel = usesBridge && plan is { ShortPortName: false }
            ? "PrintBridge" : currentPort ?? T("pending verification");
        string portHint = usesBridge ? currentPort! : "";
        if (portTip.GetToolTip(values[0]) != portHint)
        {
            portTip.SetToolTip(values[0], portHint);
        }
        values[0].Text = ((plan == null) ? T("No queue selected") : $"{plan.Queue} · {plan.Driver}\r\n{T("Current port")}: {portLabel} · {T("original")}: {plan.OriginalPort}");
        NetworkPrinter? printer = state.Printer;
        PrinterIdentity? identity = Settings.Profile?.Identity;
        values[1].Text = ((printer == null && identity == null) ? T("No device selected") : $"{printer?.Model ?? identity?.Model ?? T("Unknown model")} · {printer?.Address.ToString() ?? identity?.LastAddress} · {printer?.Host ?? identity?.Host ?? T("unnamed")}\r\nMAC: {printer?.Mac ?? identity?.Mac ?? T("unavailable")} · {printer?.Source ?? T("saved profile")} · {T("last seen")}: {Monitor.LastSuccess?.ToString("g") ?? T("unavailable")}");
        WifiAdapter? adapter = state.Adapter;
        values[2].Text = ((adapter == null) ? T("No active Wi-Fi adapter verified") : $"{adapter} · {T("index")} {adapter.Index} · {T("metric")} {adapter.Metric} · {T("gateway")} {adapter.Gateway ?? T("none")}");
        string text2 = $"{(Bridge.Running ? T("Running") : T("Stopped"))} · 127.0.0.1:{plan?.LocalPort ?? Settings.LocalPort} → {Bridge.Target?.Printer.Address.ToString() ?? T("waiting")}:9100 · {T("connections")} {Bridge.Active}";
        string? error = Bridge.LastError;
        string bridgeStatus = text2 + ((error != null) ? ($"\r\n{T("Last connection error")}: " + error) : "");
        if (bridgeDetails.Text != bridgeStatus)
        {
            bridgeDetails.Text = bridgeStatus;
        }
        string route = state.Route ?? T("Route not checked yet");
        if (routeDetails.Text != route)
        {
            routeDetails.Text = route;
        }
        configure.Text = ((Settings.Profile == null) ? T("Connect Windows queue") : T("Choose another printer"));
        test.Enabled = !Busy && Bridge.Running && Bridge.Target != null && Pending == null;
        restore.Enabled = !Busy && plan != null;
        StatusChanged?.Invoke();
    }

    private async Task RunAsync(Func<Task> work, bool background = false)
    {
        if (Busy || exiting)
        {
            return;
        }
        actions.Enabled = false;
        progress.Visible = true;
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
            progress.Visible = false;
            actions.Enabled = true;
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
            using SetupForm wizard = new SetupForm(adapters, windows, discovery, identities, Settings, stop.Token);
            if (wizard.ShowDialog(this) == DialogResult.OK)
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
                        Profile = new Profile(plan, identity),
                        Adapter = wizard.AdapterOverride
                    });
                    storage.ClearPending();
                    Pending = null;
                    await RefreshAsync(force: true);
                    if (Monitor.State.Available && !exiting && MessageBox.Show(this, T("Queue connected. Print a Windows test page?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
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
            IReadOnlyList<WifiAdapter> list = await Task.Run((Func<IReadOnlyList<WifiAdapter>>)adapters.GetAdapters, stop.Token);
            using PreferencesForm dialog = new(Settings, startup.Enabled, list, diagnostics.Clean,
                owner => CheckUpdatesRequested?.Invoke(owner) ?? Task.CompletedTask);
            var result = dialog.ShowDialog(this);
            SettingsClosed?.Invoke();
            if (result == DialogResult.OK)
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
        });
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
        using var dialog = new BugReportForm(DiagnosticsService.FormatReport(CreateReport(includeIdentifiers: false)));
        dialog.ShowDialog(this);
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
            ["WifiPresent"] = state.Adapter != null,
            ["QueuePresent"] = state.Queue != null
        };
        if (includeIdentifiers)
        {
            report["Status"] = state.Message;
            report["Route"] = state.Route;
            report["Wifi"] = state.Adapter?.ToString();
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
            portTip.Dispose();
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
