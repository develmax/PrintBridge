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

    private readonly ILogger logger;

    private int networkChanged;

    private bool closing;

    private string? previousStatus;

    private int lastColor = -1;

    private Icon? icon;

    private DateTimeOffset nextCheck;

    public TrayApplicationContext(IServiceProvider services, bool trayLaunch)
    {
        logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PrintBridge");
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
        if (!trayLaunch || !dashboard.Settings.StartHidden || dashboard.Settings.Profile == null)
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
            dashboard.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
}
