using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PrintBridge;

internal sealed record SetupSelection(WifiAdapter Adapter, PrinterQueue Queue, NetworkPrinter Device, bool Separate);

internal sealed class SetupForm : Form
{
    private readonly NetworkAdapterService adapters;

    private readonly WindowsPrinterService windows;

    private readonly PrinterDiscoveryService discovery;

    private readonly PrinterIdentityService identity;

    private readonly AppSettings settings;

    private readonly CancellationTokenSource cancellation;

    private readonly ComboBox wifi = Combo();

    private readonly ComboBox queues = Combo();

    private readonly ComboBox devices = Combo();

    private readonly RadioButton separate = new RadioButton
    {
        Text = T("Create a separate queue (recommended)"),
        Checked = true,
        AutoSize = true
    };

    private readonly RadioButton existing = new RadioButton
    {
        Text = T("Switch the selected queue and save its original port"),
        AutoSize = true
    };

    private readonly CheckBox compatible = new CheckBox
    {
        Text = T("I verified the printer and its driver's RAW TCP/9100 support."), MaximumSize = new Size(610, 0),
        AutoSize = true
    };

    private readonly Label status = new Label
    {
        AutoSize = true,
        MaximumSize = new Size(620, 0)
    };

    private readonly Button find = new Button
    {
        Text = T("Find devices"),
        AutoSize = true
    };

    private readonly Button connect = new Button
    {
        Text = T("Connect"),
        AutoSize = true,
        Enabled = false
    };

    private readonly ProgressBar progress = new ProgressBar
    {
        Style = ProgressBarStyle.Marquee,
        Width = 210,
        Visible = false
    };

    private bool busy;

    private bool closing;

    public SetupSelection? Selection { get; private set; }

    public Guid? AdapterOverride { get; private set; }

    public SetupForm(NetworkAdapterService adapters, WindowsPrinterService windows, PrinterDiscoveryService discovery, PrinterIdentityService identity, AppSettings settings, CancellationToken parent)
    {
        SetupForm setupForm = this;
        this.adapters = adapters;
        this.windows = windows;
        this.discovery = discovery;
        this.identity = identity;
        this.settings = settings;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
        cancellation.Token.Register(() =>
        {
            if (setupForm.IsHandleCreated)
            {
                try
                {
                    setupForm.BeginInvoke(setupForm.Close);
                }
                catch (InvalidOperationException)
                {
                }
            }
        });
        Text = F("PrintBridge Setup {0}", Program.Version);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(690, 485);
        MinimumSize = new Size(640, 480);
        TableLayoutPanel body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16),
            AutoScroll = true
        };
        Controls.Add(body);
        Label title = new Label
        {
            Text = T("Connect a printer"),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 17f, FontStyle.Bold)
        };
        body.Controls.Add(title);
        AddField(body, T("Wi-Fi adapter"), wifi);
        AddField(body, T("Installed Windows queue"), queues);
        AddField(body, T("Physical device"), devices);
        body.Controls.Add(separate);
        body.Controls.Add(existing);
        body.Controls.Add(compatible);
        body.Controls.Add(status);
        body.Controls.Add(progress);
        FlowLayoutPanel buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top
        };
        buttons.Controls.AddRange(find, connect, new Button
        {
            Text = T("Cancel"),
            DialogResult = DialogResult.Cancel,
            AutoSize = true
        });
        CancelButton = (Button)buttons.Controls[2];
        body.Controls.Add(buttons);
        wifi.SelectedIndexChanged += (object? _, EventArgs _) =>
        {
            setupForm.devices.Items.Clear();
            if (!setupForm.busy)
            {
                setupForm.status.Text = T("Search using the selected adapter.");
            }
            setupForm.ValidateSelection();
        };
        queues.SelectedIndexChanged += (object? _, EventArgs _) =>
        {
            setupForm.Suggest();
        };
        devices.SelectedIndexChanged += (object? _, EventArgs _) =>
        {
            setupForm.compatible.Checked = false;
            setupForm.ValidateSelection();
        };
        separate.CheckedChanged += (object? _, EventArgs _) =>
        {
            setupForm.ValidateSelection();
        };
        compatible.CheckedChanged += (object? _, EventArgs _) =>
        {
            setupForm.ValidateSelection();
        };
        find.Click += async (object? _, EventArgs _) =>
        {
            await setupForm.FindAsync();
        };
        connect.Click += (object? _, EventArgs _) =>
        {
            if (setupForm.wifi.SelectedItem is WifiAdapter wifiAdapter && setupForm.queues.SelectedItem is PrinterQueue queue && setupForm.devices.SelectedItem is NetworkPrinter device && setupForm.connect.Enabled)
            {
                setupForm.Selection = new SetupSelection(wifiAdapter, queue, device, setupForm.separate.Checked);
                setupForm.AdapterOverride = ((setupForm.wifi.Items.Count > 1 || settings.Adapter.HasValue) ? new Guid?(wifiAdapter.Id) : ((Guid?)null));
                setupForm.DialogResult = DialogResult.OK;
            }
        };
        Shown += async (object? _, EventArgs _) =>
        {
            await setupForm.FindAsync();
        };
    }

    private static ComboBox Combo()
    {
        return new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Top,
            DropDownWidth = 660
        };
    }

    private static void AddField(TableLayoutPanel body, string title, Control field)
    {
        body.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Margin = new Padding(3, 10, 3, 2)
        });
        body.Controls.Add(field);
    }

    private void Suggest()
    {
        compatible.Checked = false;
        if (queues.SelectedItem is PrinterQueue queue)
        {
            NetworkPrinter? suggestion = identity.Suggest(queue, devices.Items.Cast<NetworkPrinter>());
            if (suggestion != null)
            {
                devices.SelectedItem = suggestion;
            }
        }
        ValidateSelection();
    }

    private void ValidateSelection()
    {
        bool canConnect = false;
        if (devices.SelectedItem is NetworkPrinter device)
        {
            if (!device.Raw)
            {
                status.Text = F("TCP/9100 is unavailable. Other protocols detected: IPP {0}, LPR {1}. {2}.", YesNo(device.Ipp), YesNo(device.Lpr), device.Source);
            }
            else if (!PrinterIdentity.Confirm(device).IsTrackable)
            {
                status.Text = T("The device did not provide a MAC address, UUID, or serial number. Automatic tracking is unavailable.");
            }
            else if (queues.SelectedItem is PrinterQueue queue)
            {
                canConnect = WindowsPrinterIntegrationService.Eligible(queue) && (separate.Checked || !queue.Shared);
                status.Text = (canConnect ? T("Verify the printer's model, address, identifier, and driver compatibility.") : T("RAW printing is unavailable for the selected queue or mode."));
            }
        }
        connect.Enabled = (!busy & canConnect) && compatible.Checked;
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
        Button button = find;
        ComboBox comboBox = wifi;
        ComboBox comboBox2 = queues;
        ComboBox comboBox3 = devices;
        bool flag = (connect.Enabled = false);
        bool flag3 = (comboBox3.Enabled = flag);
        bool flag5 = (comboBox2.Enabled = flag3);
        bool enabled = (comboBox.Enabled = flag5);
        button.Enabled = enabled;
        progress.Visible = true;
        status.Text = T("Finding Wi-Fi adapters, queues, and network printers…");
        try
        {
            CancellationToken token = cancellation.Token;
            WifiAdapter? obj = wifi.SelectedItem as WifiAdapter;
            Guid? selectedAdapter = ((obj != null) ? new Guid?(obj.Id) : settings.Adapter);
            if (!closing)
            {
                IReadOnlyList<WifiAdapter> interfaces = await Task.Run((Func<IReadOnlyList<WifiAdapter>>)adapters.GetAdapters, token);
                wifi.Items.Clear();
                wifi.Items.AddRange(interfaces.Cast<object>().ToArray());
                wifi.SelectedItem = interfaces.FirstOrDefault((WifiAdapter a) =>
                {
                    Guid id = a.Id;
                    Guid? guid = selectedAdapter;
                    return id == guid;
                }) ?? interfaces.FirstOrDefault();
            }
            SpoolerState spool = await Task.Run((Func<SpoolerState>)windows.Read, token);
            string? previous = (queues.SelectedItem as PrinterQueue)?.Name;
            PrinterQueue[] available = spool.Queues.Where((PrinterQueue queue) => queue.Local && !queue.Port.StartsWith("PrintBridge_", StringComparison.OrdinalIgnoreCase)).ToArray();
            queues.Items.Clear();
            queues.Items.AddRange(available.Cast<object>().ToArray());
            queues.SelectedItem = available.FirstOrDefault((PrinterQueue q) => q.Name == previous) ?? ((available.Length == 1) ? available[0] : null);
            if (!(wifi.SelectedItem is WifiAdapter adapter))
            {
                status.Text = T("Connect to Wi-Fi and try again.");
                return;
            }
            devices.Items.Clear();
            status.Text = T("Searching the local Wi-Fi network…");
            IReadOnlyList<NetworkPrinter> found = await discovery.FindAsync(adapter, available.Select((PrinterQueue q) => q.Host).OfType<string>(), scan: true, token);
            devices.Items.AddRange(found.Cast<object>().ToArray());
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
                status.Text = ((found.Count == 0) ? T("No devices found. Check that the printer is on and reachable from this network.") : F("Found {0} devices. Select a queue and verify the physical printer.", found.Count));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex2)
        {
            status.Text = ex2.Message;
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
                progress.Visible = false;
                Button button2 = find;
                ComboBox comboBox4 = wifi;
                ComboBox comboBox5 = queues;
                flag3 = (devices.Enabled = true);
                flag5 = (comboBox5.Enabled = flag3);
                enabled = (comboBox4.Enabled = flag5);
                button2.Enabled = enabled;
                ValidateSelection();
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy)
        {
            closing = true;
            cancellation.Cancel();
            e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            cancellation.Dispose();
        }
        base.Dispose(disposing);
    }
}
