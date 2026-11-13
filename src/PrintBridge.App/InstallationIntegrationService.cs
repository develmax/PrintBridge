using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PrintBridge;

public sealed class InstallationIntegrationService(SettingsService settings, StartupService startup, WindowsPrinterIntegrationService integration, WindowsPrinterService windows, NetworkAdapterService adapters, PrinterDiscoveryService discovery, ILogger<InstallationIntegrationService> logger)
{
    public Task ChangeQueueAsync(string action, PrintPlan plan)
    {
        return Task.Run(() =>
        {
            ChangeQueue(action, plan);
        });
    }

    private void ChangeQueue(string action, PrintPlan plan)
    {
        string payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(plan));
        ProcessStartInfo start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        start.ArgumentList.Add("--spooler");
        start.ArgumentList.Add(action);
        start.ArgumentList.Add(payload);
        start.ArgumentList.Add(Normalize(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName));
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException(T("Could not start the Windows print operation."));
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(T("Windows could not complete the queue operation. See the operation dialog for details."));
            }
            logger.LogInformation("Spooler operation {Action} for profile {Profile}", action, plan.Id);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException(T("Administrator approval was canceled."));
        }
    }

    internal int RunElevated(string[] args)
    {
        if (args.Length != 4 || args[2].Length > 10000 || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException(T("This operation requires administrator privileges."));
        }
        PrintPlan plan = JsonSerializer.Deserialize<PrintPlan>(Convert.FromBase64String(args[2])) ?? throw new InvalidOperationException(T("Operation parameters are missing."));
        string text = args[1];
        if (!(text == "connect"))
        {
            if (!(text == "restore"))
            {
                throw new InvalidOperationException(T("Unknown spooler operation."));
            }
            integration.Restore(plan);
        }
        else
        {
            integration.Connect(plan);
        }
        return 0;
    }

    internal static bool Signal(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(Program.SessionName + name, out EventWaitHandle? signal))
        {
            return false;
        }
        using (signal)
        {
            return signal.Set();
        }
    }

    internal bool StopInstance(bool forUpdate = false)
    {
        if (!Mutex.TryOpenExisting(Program.SessionName, out Mutex? mutex))
        {
            return true;
        }
        using (mutex)
        {
            Signal(forUpdate ? ".StopForUpdate" : ".Stop");
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(45)))
                {
                    return false;
                }
                mutex.ReleaseMutex();
                return true;
            }
            catch (AbandonedMutexException)
            {
                mutex.ReleaseMutex();
                return true;
            }
        }
    }

    internal int Uninstall(bool quiet)
    {
        if (!StopInstance())
        {
            throw new InvalidOperationException(T("Wait for PrintBridge to finish its current operation before uninstalling."));
        }
        PrintPlan? plan = settings.Pending() ?? settings.Load().Profile?.Print;
        if (plan != null)
        {
            string message = (plan.Separate ? F("Uninstall will remove '{0}' if it still uses the bridge. Continue?", plan.Queue) : F("Uninstall will restore the original port for '{0}'. Continue?", plan.Queue));
            if (quiet || MessageBox.Show(message, "PrintBridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return 2;
            }
            ChangeQueue("restore", plan);
            settings.Save(settings.Load() with
            {
                Profile = null
            });
            settings.ClearPending();
        }
        startup.Enabled = false;
        if (!quiet && MessageBox.Show(T("Delete saved PrintBridge settings and logs?"), "PrintBridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            if (Directory.Exists(SettingsService.LogFolder))
            {
                foreach (string item in Directory.EnumerateFiles(SettingsService.LogFolder, "printbridge*.jsonl"))
                {
                    File.Delete(item);
                }
            }
            File.Delete(Path.Combine(SettingsService.Folder, "settings.json"));
        }
        return 0;
    }

    internal int CheckSystem(bool checkNetwork, bool scan)
    {
        Dictionary<string, object> result = new Dictionary<string, object>();
        bool failed = false;
        IReadOnlyList<WifiAdapter> wifi = Array.Empty<WifiAdapter>();
        try
        {
            wifi = adapters.GetAdapters();
            result["Wifi"] = wifi.Count;
        }
        catch (Exception ex)
        {
            result["WifiError"] = ex.Message;
            failed = true;
        }
        try
        {
            SpoolerState state = windows.Read();
            result["Queues"] = state.Queues.Count;
            result["Ports"] = state.Ports.Count;
            if (checkNetwork)
            {
                WifiAdapter? adapter = wifi.FirstOrDefault();
                if (adapter != null)
                {
                    using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25L));
                    string[] hints = (from q in state.Queues
                        where q.RawPort == true
                        select q.Host).OfType<string>().ToArray();
                    IReadOnlyList<NetworkPrinter> devices = discovery.FindAsync(adapter, hints, scan, timeout.Token).GetAwaiter().GetResult();
                    result["KnownRawQueues"] = hints.Length;
                    result["RawCandidates"] = Enumerable.Count(devices, (NetworkPrinter printer) => printer.Raw);
                    result["IdentifiedRawCandidates"] = Enumerable.Count(devices, (NetworkPrinter printer) => printer.Raw && PrinterIdentity.Confirm(printer).IsTrackable);
                    result["WifiMetric"] = adapter.Metric;
                    NetworkPrinter? raw = devices.FirstOrDefault((NetworkPrinter printer) => printer.Raw);
                    if (raw != null)
                    {
                        result["Route"] = new RoutingDiagnosticsService().Describe(adapter, raw.Address);
                    }
                }
            }
        }
        catch (Exception ex2)
        {
            result["SpoolerError"] = ex2.Message;
            failed = true;
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "system-check.json"), JsonSerializer.Serialize(result));
        return failed ? 1 : 0;
    }
}
