using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PrintBridge;

internal sealed class PreferencesForm : Form
{
    private sealed record AdapterChoice(Guid? Id, string Name)
    {
        public override string ToString()
        {
            return Name;
        }
    }

    private readonly AppSettings initial;

    private readonly CheckBox startup;

    private readonly CheckBox hidden;

    private readonly CheckBox notify;

    private readonly CheckBox search;

    private readonly CheckBox verbose;
    private readonly CheckBox autoUpdate;

    private readonly ComboBox language = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill,
        DisplayMember = nameof(LanguageOption.Name),
        ValueMember = nameof(LanguageOption.Code)
    };

    private readonly ComboBox wifi = new ComboBox
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill
    };

    private readonly NumericUpDown interval;

    private readonly NumericUpDown local;

    private readonly NumericUpDown connect;

    private readonly NumericUpDown idle;

    private readonly NumericUpDown limit;

    public AppSettings Value => initial with
    {
        StartHidden = hidden.Checked,
        Notify = notify.Checked,
        AutoDiscover = search.Checked,
        Verbose = verbose.Checked,
        AutoUpdate = autoUpdate.Checked,
        Language = (string)language.SelectedValue!,
        Adapter = ((AdapterChoice)wifi.SelectedItem!).Id,
        IntervalSeconds = (int)interval.Value,
        LocalPort = (int)local.Value,
        ConnectSeconds = (int)connect.Value,
        IdleSeconds = (int)idle.Value,
        MaxConnections = (int)limit.Value
    };

    public bool StartupEnabled => startup.Checked;

    public PreferencesForm(AppSettings settings, bool runAtStartup, IReadOnlyList<LanAdapter> adapters, Action clean,
        Func<IWin32Window, Task> checkUpdates)
    {
        PreferencesForm preferencesForm = this;
        initial = settings;
        Text = T("PrintBridge Settings");
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(610, 550);
        MinimumSize = new Size(590, 500);
        TableLayoutPanel body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 2,
            Padding = new Padding(14)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49f));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51f));
        Controls.Add(body);
        language.DataSource = Languages.ToArray();
        language.SelectedValue = Normalize(settings.Language);
        Row(T("Language"), language);
        startup = Check(T("Start with Windows"), runAtStartup);
        hidden = Check(T("Start minimized to the tray"), settings.StartHidden);
        notify = Check(T("Show tray notifications"), settings.Notify);
        search = Check(T("Find new IP addresses automatically"), settings.AutoDiscover);
        verbose = Check(T("Verbose logging"), settings.Verbose);
        autoUpdate = Check(T("Automatically check for and install updates"), settings.AutoUpdate);
        var checkUpdate = new Button { AutoSize = true, Text = T("Check for updates now") };
        checkUpdate.Click += async (_, _) =>
        {
            checkUpdate.Enabled = false;
            try
            {
                await checkUpdates(this);
            }
            finally
            {
                if (!IsDisposed && !Disposing)
                    checkUpdate.Enabled = true;
            }
        };
        body.Controls.Add(checkUpdate);
        body.SetColumnSpan(checkUpdate, 2);
        interval = Number(T("Idle check interval (seconds)"), settings.IntervalSeconds, 15, 3600);
        local = Number(T("Local port"), settings.LocalPort, 1024, 65535);
        local.Enabled = settings.Profile == null;
        connect = Number(T("Connect timeout (seconds)"), settings.ConnectSeconds, 1, 30);
        idle = Number(T("Idle timeout (seconds)"), settings.IdleSeconds, 15, 1800);
        limit = Number(T("Concurrent connections"), settings.MaxConnections, 1, 16);
        wifi.Items.Add(new AdapterChoice(null, T("Automatic")));
        foreach (LanAdapter adapter in adapters.DistinctBy((LanAdapter a) => a.Id))
        {
            wifi.Items.Add(new AdapterChoice(adapter.Id, adapter.ToString()));
        }
        Guid? adapter2 = settings.Adapter;
        if (adapter2.HasValue)
        {
            Guid id = adapter2.GetValueOrDefault();
            if (!adapters.Any((LanAdapter a) => a.Id == id))
            {
                wifi.Items.Add(new AdapterChoice(id, T("Saved adapter unavailable")));
            }
        }
        wifi.SelectedItem = wifi.Items.Cast<AdapterChoice>().First((AdapterChoice a) =>
        {
            Guid? id2 = a.Id;
            Guid? adapter3 = settings.Adapter;
            if (id2.HasValue != adapter3.HasValue)
            {
                return false;
            }
            return !id2.HasValue || id2.GetValueOrDefault() == adapter3.GetValueOrDefault();
        });
        Row(T("Network adapter"), wifi);
        Row(T("Print protocol"), new Label
        {
            Text = "RAW TCP/9100",
            AutoSize = true
        });
        Profile? profile = settings.Profile;
        if (profile != null)
        {
            Label selected = new Label
            {
                Text = profile.Print.Queue + "\r\n" + (profile.Identity.Mac ?? profile.Identity.Serial ?? profile.Identity.DeviceId),
                AutoSize = true
            };
            Row(T("Queue and identifier"), selected);
        }
        Button clear = new Button
        {
            Text = T("Delete old logs"),
            AutoSize = true
        };
        clear.Click += (object? _, EventArgs _) =>
        {
            try
            {
                clean();
            }
            catch (Exception ex)
            {
                MessageBox.Show(preferencesForm, ex.Message, preferencesForm.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
        };
        body.Controls.Add(clear);
        FlowLayoutPanel buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft
        };
        Button ok = new Button
        {
            Text = T("Save"),
            DialogResult = DialogResult.OK,
            AutoSize = true
        };
        Button cancel = new Button
        {
            Text = T("Cancel"),
            DialogResult = DialogResult.Cancel,
            AutoSize = true
        };
        buttons.Controls.AddRange(ok, cancel);
        body.Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        CheckBox Check(string title, bool enabled)
        {
            CheckBox control = new CheckBox
            {
                Text = title,
                Checked = enabled,
                AutoSize = true
            };
            body.Controls.Add(control);
            body.SetColumnSpan(control, 2);
            return control;
        }
        NumericUpDown Number(string title, int value, int min, int max)
        {
            NumericUpDown control = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                Width = 95
            };
            Row(title, control);
            return control;
        }
        void Row(string title, Control control)
        {
            body.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                Anchor = AnchorStyles.Left
            });
            body.Controls.Add(control);
        }
    }
}
