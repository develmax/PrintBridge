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
            if (args.FirstOrDefault() == "--stop")
            {
                bool running = Mutex.TryOpenExisting(SessionName, out var instance);
                instance?.Dispose();
                return install.StopInstance() ? (running ? 3 : 0) : 1;
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
                using var context = new TrayApplicationContext(provider, args.Contains("--tray"));
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
            if (!args.Contains("--quiet"))
                MessageBox.Show(ex.Message, "PrintBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
