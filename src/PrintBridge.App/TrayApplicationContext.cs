using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PrintBridge;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Dashboard dashboard;

    private readonly NotifyIcon tray;

    private readonly ToolStripMenuItem status = new ToolStripMenuItem(T("Select a printer"))
    {
        Enabled = false
    };

    private readonly ToolStripMenuItem toggle = new ToolStripMenuItem(T("Stop bridge"));

    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer
    {
        Interval = 1000
    };

    private readonly EventWaitHandle stopEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, Program.SessionName + ".Stop");

    private readonly EventWaitHandle showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, Program.SessionName + ".Show");
    private readonly EventWaitHandle updateStopEvent = new(false, EventResetMode.AutoReset, Program.SessionName + ".StopForUpdate");
    private readonly CancellationTokenSource lifetime = new();
    private readonly WindowsPrinterService windows;
    private readonly GitHubUpdateService updates = new();
    private readonly bool msi = File.Exists(Path.Combine(AppContext.BaseDirectory, "msi-installation.txt"));
    private DateTimeOffset nextUpdate = DateTimeOffset.UtcNow.AddMinutes(1);
    private bool checkingUpdate;
    private bool preparingUpdate;
    private Version? queuedUpdate;
    private bool updateApproved;

    private readonly ILogger logger;

    private int networkChanged;

    private bool closing;
    private bool quietStartup;

    private string? previousStatus;

    private Color lastColor = Color.Empty;

    private Icon? icon;

    private DateTimeOffset nextCheck;

    public TrayApplicationContext(IServiceProvider services, bool trayLaunch, bool afterUpdate)
    {
        quietStartup = trayLaunch;
        logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PrintBridge");
        windows = services.GetRequiredService<WindowsPrinterService>();
        dashboard = ActivatorUtilities.CreateInstance<Dashboard>(services, Array.Empty<object>());
        dashboard.CheckUpdatesRequested += CheckForUpdateAsync;
        dashboard.SettingsClosed += () =>
        {
            if (queuedUpdate != null)
                nextUpdate = DateTimeOffset.UtcNow;
        };
        ContextMenuStrip menu = new ContextMenuStrip
        {
            Items =
            {
                {
                    T("Open PrintBridge"),
                    (Image?)null,
                    (EventHandler?)((object? _, EventArgs _) =>
                    {
                        Open();
                    })
                },
                (ToolStripItem)status,
                (ToolStripItem)new ToolStripSeparator(),
                {
                    T("Check connection"),
                    (Image?)null,
                    (EventHandler?)(async (object? _, EventArgs _) =>
                    {
                        await dashboard.CheckAsync(force: false);
                    })
                },
                {
                    T("Search again"),
                    (Image?)null,
                    (EventHandler?)(async (object? _, EventArgs _) =>
                    {
                        await dashboard.CheckAsync(force: true);
                    })
                },
                {
                    T("Restart bridge"),
                    (Image?)null,
                    (EventHandler?)(async (object? _, EventArgs _) =>
                    {
                        await dashboard.RestartAsync();
                    })
                }
            }
        };
        toggle.Click += async (object? _, EventArgs _) =>
        {
            await dashboard.ToggleAsync();
        };
        menu.Items.Add(toggle);
        menu.Items.Add(T("Settings"), null, async (object? _, EventArgs _) =>
        {
            Open();
            await dashboard.SettingsAsync();
        });
        menu.Items.Add(T("Open logs"), null, (object? _, EventArgs _) =>
        {
            dashboard.OpenLogs();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T("Exit"), null, async (object? _, EventArgs _) =>
        {
            await ShutdownAsync();
        });
        tray = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "PrintBridge " + Program.Version,
            Icon = dashboard.Icon,
            Visible = true
        };
        tray.DoubleClick += (object? _, EventArgs _) =>
        {
            Open();
        };
        dashboard.StatusChanged += Render;
        dashboard.ExitRequested += async () =>
        {
            await ShutdownAsync();
        };
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        timer.Tick += async (object? _, EventArgs _) =>
        {
            await TickAsync();
        };
        timer.Start();
        logger.LogInformation("PrintBridge {Version} started", Program.Version);
        Render();
        if (!afterUpdate && (!trayLaunch || !dashboard.Settings.StartHidden || dashboard.Settings.Profile == null))
        {
            Open();
        }
        if (!trayLaunch && dashboard.Settings.Profile == null && dashboard.Pending == null)
        {
            dashboard.BeginInvoke(async () =>
            {
                await dashboard.ConfigureAsync();
            });
        }
    }

    private void Open()
    {
        if (!closing)
        {
            dashboard.Show();
            if (dashboard.WindowState == FormWindowState.Minimized)
            {
                dashboard.WindowState = FormWindowState.Normal;
            }
            dashboard.Activate();
        }
    }

    private async Task TickAsync()
    {
        if (closing)
        {
            return;
        }
        if (stopEvent.WaitOne(0))
        {
            await ShutdownAsync();
            return;
        }
        if (showEvent.WaitOne(0))
        {
            Open();
        }
        if (updateStopEvent.WaitOne(0))
        {
            if (preparingUpdate)
                return;
            preparingUpdate = true;
            if (await CanUpdateAsync() && !closing)
            {
                await ShutdownAsync();
                return;
            }
            preparingUpdate = false;
        }
        if (preparingUpdate)
            return;
        if ((dashboard.Settings.AutoUpdate || updateApproved) && !checkingUpdate &&
            (queuedUpdate == null || !dashboard.Busy) && DateTimeOffset.UtcNow >= nextUpdate)
            _ = CheckForUpdateAsync();
        dashboard.Render();
        if (!dashboard.Busy && dashboard.Pending == null && dashboard.Settings.Profile != null && !dashboard.Monitor.Paused)
        {
            bool networkEvent = Interlocked.Exchange(ref networkChanged, 0) != 0;
            if (networkEvent || (dashboard.Bridge.Active == 0 && DateTimeOffset.UtcNow >= nextCheck))
            {
                nextCheck = DateTimeOffset.UtcNow.AddSeconds(dashboard.Settings.IntervalSeconds);
                await dashboard.CheckAsync(networkEvent, background: true);
                if (quietStartup && !closing)
                {
                    previousStatus = dashboard.Monitor.State.Message;
                    quietStartup = false;
                }
            }
        }
    }

    private async Task<bool> CanUpdateAsync()
    {
        if (closing || !(dashboard.Settings.AutoUpdate || updateApproved) || dashboard.Busy || dashboard.Pending != null || dashboard.Bridge.Active != 0)
            return false;

        var profile = dashboard.Settings.Profile;
        try
        {
            return (profile == null || !await Task.Run(() => windows.HasJobs(profile.Print.Queue))) &&
                !closing && (dashboard.Settings.AutoUpdate || updateApproved) && !dashboard.Busy && dashboard.Pending == null &&
                dashboard.Settings.Profile == profile && dashboard.Bridge.Active == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check the print queue for an update");
            return false;
        }
    }

    private async Task CheckForUpdateAsync(IWin32Window? owner = null)
    {
        if (checkingUpdate)
        {
            if (owner != null)
                ShowUpdateMessage(owner, T("An update check is already in progress."));
            return;
        }
        checkingUpdate = true;
        try
        {
            if (owner != null || queuedUpdate == null)
                nextUpdate = DateTimeOffset.UtcNow.AddHours(12);
            var current = Version.Parse(Program.Version);
            if (owner != null || queuedUpdate == null)
            {
                using var checkDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                checkDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                var available = await updates.CheckAsync(current, msi, checkDeadline.Token);
                if (closing || owner is Form { IsDisposed: true })
                    return;
                if (available == null)
                {
                    queuedUpdate = null;
                    updateApproved = false;
                    if (owner != null)
                        ShowUpdateMessage(owner, T("No newer compatible release is available."));
                    return;
                }
                if (!dashboard.Settings.AutoUpdate && (!updateApproved || queuedUpdate != available))
                {
                    if (owner == null || MessageBox.Show(owner, F("PrintBridge {0} is available. Install it when printing finishes?", available),
                        "PrintBridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                        return;
                    updateApproved = true;
                }
                if (queuedUpdate != available || owner != null)
                    tray.ShowBalloonTip(4000, "PrintBridge", F("PrintBridge {0} will install when printing finishes.", available), ToolTipIcon.Info);
                queuedUpdate = available;
            }
            if (!(dashboard.Settings.AutoUpdate || updateApproved))
                return;
            nextUpdate = DateTimeOffset.UtcNow.AddMinutes(5);
            if (!await CanUpdateAsync())
                return;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            string? package = await updates.DownloadAsync(current, msi, deadline.Token, queuedUpdate);
            if (package == null)
            {
                queuedUpdate = null;
                updateApproved = false;
                return;
            }
            if (await CanUpdateAsync())
            {
                using var worker = StartUpdateWorker(package);
                await worker.WaitForExitAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update did not start");
            if (owner != null && !closing && owner is not Form { IsDisposed: true })
                ShowUpdateMessage(owner, T("Could not check for updates. Try again later."), MessageBoxIcon.Warning);
        }
        finally
        {
            checkingUpdate = false;
        }
    }

    private static void ShowUpdateMessage(IWin32Window owner, string message, MessageBoxIcon icon = MessageBoxIcon.Information) =>
        MessageBox.Show(owner, message, "PrintBridge", MessageBoxButtons.OK, icon);

    private static System.Diagnostics.Process StartUpdateWorker(string package)
    {
        string worker = Path.Combine(Path.GetDirectoryName(package)!, "PrintBridge.Update.exe");
        File.Copy(Environment.ProcessPath!, worker, overwrite: true);
        var start = new System.Diagnostics.ProcessStartInfo(worker) { UseShellExecute = false };
        start.ArgumentList.Add("--install-update");
        start.ArgumentList.Add(package);
        start.ArgumentList.Add(Environment.ProcessPath!);
        return System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the update worker.");
    }

    private void NetworkAddressChanged(object? sender, EventArgs args)
    {
        if (!dashboard.IsDisposed && dashboard.IsHandleCreated)
        {
            try
            {
                dashboard.BeginInvoke(() =>
                {
                    dashboard.InvalidateNetwork();
                    Interlocked.Exchange(ref networkChanged, 1);
                });
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
        Interlocked.Exchange(ref networkChanged, 1);
    }

    private void Render()
    {
        MonitorState state = dashboard.Monitor.State;
        status.Text = state.Message;
        toggle.Text = (dashboard.Monitor.Paused ? T("Start bridge") : T("Stop bridge"));
        Color color = dashboard.StatusColor;
        if (color != lastColor)
        {
            using var trayIcon = new Icon(dashboard.Icon ?? SystemIcons.Application, SystemInformation.SmallIconSize);
            using var bitmap = new Bitmap(trayIcon.Width, trayIcon.Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.DrawIcon(trayIcon, new Rectangle(Point.Empty, bitmap.Size));
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int diameter = Math.Max(6, bitmap.Width / 3);
                using SolidBrush outline = new SolidBrush(SystemColors.Control);
                graphics.FillEllipse(outline, bitmap.Width - diameter, bitmap.Height - diameter, diameter, diameter);
                using SolidBrush fill = new SolidBrush(color);
                graphics.FillEllipse(fill, bitmap.Width - diameter + 1, bitmap.Height - diameter + 1, diameter - 2, diameter - 2);
            }
            nint handle = bitmap.GetHicon();
            using Icon native = Icon.FromHandle(handle);
            Icon updated = (Icon)native.Clone();
            DestroyIcon(handle);
            tray.Icon = updated;
            icon?.Dispose();
            icon = updated;
            lastColor = color;
        }
        if (!state.Searching && state.Message != previousStatus)
        {
            if (!quietStartup && previousStatus != null && dashboard.Settings.Notify && dashboard.Settings.Profile != null && !dashboard.Visible)
            {
                tray.ShowBalloonTip(2500, "PrintBridge", state.Message, ToolTipIcon.Info);
            }
            previousStatus = state.Message;
        }
    }

    private async Task ShutdownAsync()
    {
        if (!closing)
        {
            closing = true;
            lifetime.Cancel();
            timer.Stop();
            tray.Visible = false;
            await dashboard.ShutdownAsync();
            logger.LogInformation("Bridge and app stopped");
            ExitThread();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
            timer.Dispose();
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
            icon?.Dispose();
            stopEvent.Dispose();
            showEvent.Dispose();
            updateStopEvent.Dispose();
            lifetime.Dispose();
            dashboard.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
}
