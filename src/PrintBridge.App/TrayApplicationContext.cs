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

    private readonly ILogger logger;

    private int networkChanged;

    private bool closing;

    private string? previousStatus;

    private int lastColor = -1;

    private Icon? icon;

    private DateTimeOffset nextCheck;

    public TrayApplicationContext(IServiceProvider services, bool trayLaunch, bool afterUpdate)
    {
        logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PrintBridge");
        windows = services.GetRequiredService<WindowsPrinterService>();
        dashboard = ActivatorUtilities.CreateInstance<Dashboard>(services, Array.Empty<object>());
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
        if (dashboard.Settings.AutoUpdate && !checkingUpdate && DateTimeOffset.UtcNow >= nextUpdate)
            _ = CheckForUpdateAsync();
        dashboard.Render();
        if (!dashboard.Busy && dashboard.Pending == null && dashboard.Settings.Profile != null && !dashboard.Monitor.Paused)
        {
            bool networkEvent = Interlocked.Exchange(ref networkChanged, 0) != 0;
            if (networkEvent || !(DateTimeOffset.UtcNow < nextCheck))
            {
                nextCheck = DateTimeOffset.UtcNow.AddSeconds(dashboard.Settings.IntervalSeconds);
                await dashboard.CheckAsync(networkEvent, background: true);
            }
        }
    }

    private async Task<bool> CanUpdateAsync()
    {
        if (closing || !dashboard.Settings.AutoUpdate || dashboard.Busy || dashboard.Pending != null || dashboard.Bridge.Active != 0)
            return false;

        var profile = dashboard.Settings.Profile;
        try
        {
            return (profile == null || !await Task.Run(() => windows.HasJobs(profile.Print.Queue))) &&
                !closing && dashboard.Settings.AutoUpdate && !dashboard.Busy && dashboard.Pending == null &&
                dashboard.Settings.Profile == profile && dashboard.Bridge.Active == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check the print queue for an update");
            return false;
        }
    }

    private async Task CheckForUpdateAsync()
    {
        checkingUpdate = true;
        nextUpdate = DateTimeOffset.UtcNow.AddHours(12);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            string? package = await updates.DownloadAsync(Version.Parse(Program.Version), msi, deadline.Token);
            if (package == null || closing || !dashboard.Settings.AutoUpdate)
                return;
            if (!await CanUpdateAsync())
            {
                nextUpdate = DateTimeOffset.UtcNow.AddMinutes(5);
                return;
            }
            StartUpdateWorker(package);
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Automatic update did not start");
        }
        finally
        {
            checkingUpdate = false;
        }
    }

    private static void StartUpdateWorker(string package)
    {
        string worker = Path.Combine(Path.GetDirectoryName(package)!, "PrintBridge.Update.exe");
        File.Copy(Environment.ProcessPath!, worker, overwrite: true);
        var start = new System.Diagnostics.ProcessStartInfo(worker) { UseShellExecute = false };
        start.ArgumentList.Add("--install-update");
        start.ArgumentList.Add(package);
        start.ArgumentList.Add(Environment.ProcessPath!);
        using var process = System.Diagnostics.Process.Start(start)
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
        int color = ((!dashboard.Monitor.Paused && dashboard.Settings.Profile != null) ? (state.Searching ? 1 : ((state.Available && dashboard.Bridge.LastError == null) ? 2 : 3)) : 0);
        if (color != lastColor)
        {
            Color tint = color switch
            {
                1 => Color.Goldenrod,
                2 => Color.ForestGreen,
                3 => Color.Firebrick,
                _ => Color.Gray,
            };
            using Bitmap bitmap = (dashboard.Icon ?? SystemIcons.Application).ToBitmap();
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                using SolidBrush fill = new SolidBrush(tint);
                graphics.FillEllipse(fill, bitmap.Width - 14, bitmap.Height - 14, 13, 13);
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
            if (previousStatus != null && dashboard.Settings.Notify && dashboard.Settings.Profile != null && !dashboard.Visible)
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
