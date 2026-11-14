using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Principal;

namespace PrintBridge;

internal static class Program
{
    public static string SessionName => @"Local\PrintBridge." + WindowsIdentity.GetCurrent().User!.Value;
    internal static string Version => typeof(Program).Assembly.GetName().Version!.ToString(3);
    internal static bool RestartRequested { get; set; }

    [STAThread]
    private static int Main(string[] args)
    {
        Select(null);
        ApplicationConfiguration.Initialize();
        var services = new ServiceCollection();
        services.AddSingleton<DiagnosticsService>();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<ILoggerProvider>(provider => provider.GetRequiredService<DiagnosticsService>());
        services.AddSingleton<SettingsService>();
        services.AddSingleton<StartupService>();
        services.AddSingleton<NetworkAdapterService>();
        services.AddSingleton<RoutingDiagnosticsService>();
        services.AddSingleton<PrinterDiscoveryService>();
        services.AddSingleton<PrinterIdentityService>();
        services.AddSingleton<TcpBridgeService>();
        services.AddSingleton<WindowsPrinterService>();
        services.AddSingleton<ISpooler>(provider => provider.GetRequiredService<WindowsPrinterService>());
        services.AddSingleton<WindowsPrinterIntegrationService>();
        services.AddSingleton<InstallationIntegrationService>();
        services.AddSingleton<PrinterMonitorService>();
        using var provider = services.BuildServiceProvider();
        var install = provider.GetRequiredService<InstallationIntegrationService>();
        try
        {
#if SIGNED_RELEASE
            if (args.FirstOrDefault() is not ("--stop" or "--stop-for-update" or "--msi-stop" or "--uninstall"))
            {
                Select(args.FirstOrDefault() == "--spooler" && args.Length == 4
                    ? args[3] : provider.GetRequiredService<SettingsService>().Load().Language);
                if (!ReleaseSignature.VerifyExecutable(Environment.ProcessPath!))
                {
                    string message = T("PrintBridge could not verify its signature. Reinstall it from the official release.");
                    provider.GetRequiredService<ILoggerFactory>().CreateLogger("PrintBridge").LogError("{Message}", message);
                    if (!args.Any(arg => arg is "--quiet" or "--tray" or "--after-update" or "--install-update"))
                        MessageBox.Show(message, "PrintBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
#endif
            if (args.FirstOrDefault() == "--install-update" && args.Length == 3)
            {
                bool msi = Path.GetExtension(args[1]) == ".msi";
                try
                {
                    return UpdateInstaller.InstallAsync(args[1], msi,
                        provider.GetRequiredService<ILoggerFactory>().CreateLogger("PrintBridge")).GetAwaiter().GetResult();
                }
                finally
                {
                    if (Mutex.TryOpenExisting(SessionName, out var runningInstance))
                        runningInstance.Dispose();
                    else if (File.Exists(args[2]))
                        Process.Start(new ProcessStartInfo(args[2], "--after-update") { UseShellExecute = false });
                }
            }
            if (args.FirstOrDefault() == "--msi-stop")
            {
                bool uninstall = args.Contains("--remove");
                if (uninstall && (provider.GetRequiredService<SettingsService>().Pending() != null ||
                    provider.GetRequiredService<SettingsService>().Load().Profile != null))
                    return 1;
                if (!install.StopInstance(args.Contains("--automatic")))
                    return 1;
                if (uninstall)
                    provider.GetRequiredService<StartupService>().Enabled = false;
                return 0;
            }
            if (args.FirstOrDefault() is "--stop" or "--stop-for-update")
            {
                bool running = Mutex.TryOpenExisting(SessionName, out var instance);
                instance?.Dispose();
                return install.StopInstance(forUpdate: args[0] == "--stop-for-update") ? (running ? 3 : 0) : 1;
            }

            Select(args.FirstOrDefault() == "--spooler" && args.Length == 4
                ? args[3] : provider.GetRequiredService<SettingsService>().Load().Language);
            switch (args.FirstOrDefault())
            {
                case "--spooler": return install.RunElevated(args);
                case "--uninstall": return install.Uninstall(args.Contains("--quiet"));
                case "--check-system":
                    return install.CheckSystem(args.Contains("--network") || args.Contains("--scan"), args.Contains("--scan"));
            }

            using (var mutex = new Mutex(true, SessionName, out bool first))
            {
                if (!first)
                {
                    InstallationIntegrationService.Signal(".Show");
                    return 0;
                }
                bool afterUpdate = args.Contains("--after-update");
                using var context = new TrayApplicationContext(provider, args.Contains("--tray") || afterUpdate, afterUpdate);
                Application.Run(context);
            }
            if (RestartRequested)
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            if (args.FirstOrDefault() != "--spooler")
                provider.GetRequiredService<ILoggerFactory>().CreateLogger("PrintBridge").LogError(ex, "Application error");
            if (!args.Contains("--quiet") && args.FirstOrDefault() != "--install-update")
                MessageBox.Show(ex.Message, "PrintBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
