using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBridge;

public sealed record MonitorState(string Message, bool Searching = false, bool Available = false, WifiAdapter? Adapter = null, NetworkPrinter? Printer = null, PrinterQueue? Queue = null, string? Route = null);

public sealed class PrinterMonitorService(SettingsService storage, NetworkAdapterService network, PrinterDiscoveryService discovery, PrinterIdentityService identities, WindowsPrinterService windows, RoutingDiagnosticsService routing, TcpBridgeService bridge, ILogger<PrinterMonitorService> logger)
{
    private DateTimeOffset nextScan;

    private Guid? lastAdapter;

    private string? lastAddress;

    private string? lastRoute;

    private bool previouslyAvailable;

    public MonitorState State { get; private set; } = new MonitorState(T("Select a printer."));

    public DateTimeOffset? LastSuccess { get; private set; }

    public bool Paused { get; set; }

    public event Action? Changed;

    private void Update(MonitorState value)
    {
        State = value;
        Changed?.Invoke();
    }

    public async Task RefreshAsync(AppSettings settings, bool force, CancellationToken token)
    {
        Profile? profile = settings.Profile;
        if (profile == null)
        {
            bridge.SetTarget(null);
            Update(new MonitorState(T("Select a printer and connect its Windows queue.")));
            return;
        }
        if (Paused)
        {
            bridge.SetTarget(null);
            Update(State with
            {
                Message = T("The bridge is stopped."),
                Available = false,
                Searching = false
            });
            return;
        }
        Update(State with
        {
            Searching = true,
            Message = T("Checking connection…")
        });
        WifiAdapter? adapter = null;
        PrinterQueue? queue = null;
        string? route = null;
        try
        {
            Task<IReadOnlyList<WifiAdapter>> wifiTask = Task.Run((Func<IReadOnlyList<WifiAdapter>>)network.GetAdapters, token);
            Task<SpoolerState> spoolTask = Task.Run((Func<SpoolerState>)windows.Read, token);
            await Task.WhenAll(wifiTask, spoolTask);
            IReadOnlyList<WifiAdapter> wifi = await wifiTask;
            SpoolerState spool = await spoolTask;
            queue = spool.Queue(profile.Print.Queue);
            BridgeTarget? previous = bridge.Target;
            Guid? adapter2 = settings.Adapter;
            WifiAdapter? wifiAdapter;
            if (adapter2.HasValue)
            {
                Guid id = adapter2.GetValueOrDefault();
                wifiAdapter = wifi.FirstOrDefault((WifiAdapter item) => item.Id == id);
            }
            else
            {
                wifiAdapter = wifi.FirstOrDefault((WifiAdapter item) =>
                {
                    Guid id3 = item.Id;
                    Guid? guid = previous?.Adapter.Id;
                    return id3 == guid;
                }) ?? wifi.FirstOrDefault((WifiAdapter item) => IPAddress.TryParse(profile.Identity.LastAddress, out IPAddress? address) && item.Contains(address)) ?? wifi.FirstOrDefault();
            }
            adapter = wifiAdapter;
            if (!WindowsPrinterIntegrationService.Verify(profile.Print, spool))
            {
                bridge.SetTarget(null);
                Update(new MonitorState(T("The Windows queue or port is missing or has changed. Check the queue configuration."), Searching: false, Available: false, null, null, queue));
                return;
            }
            EnsureListener(settings, profile.Print.LocalPort);
            if (adapter == null)
            {
                bridge.SetTarget(null);
                Update(new MonitorState(T("Waiting for the selected Wi-Fi connection."), Searching: false, Available: false, null, null, queue));
                previouslyAvailable = false;
                return;
            }
            IPAddress? current = (IPAddress.TryParse(profile.Identity.LastAddress, out IPAddress? parsed) ? parsed : null);
            if (previous != null && (previous.Adapter.Id != adapter.Id || !previous.Adapter.Address.Equals(adapter.Address)))
            {
                bridge.SetTarget(null);
            }
            if (current != null && RoutingDiagnosticsService.IsCandidate(adapter, current))
            {
                NetworkPrinter known = new NetworkPrinter(current, profile.Identity.Host, profile.Identity.Model, null, null, null, Raw: false, Ipp: false, Lpr: false, T("Saved address"));
                bridge.SetTarget(new BridgeTarget(adapter, known, profile.Identity));
            }
            else
            {
                bridge.SetTarget(null);
            }
            route = routing.Describe(adapter, current);
            Guid id2 = adapter.Id;
            adapter2 = lastAdapter;
            if (id2 != adapter2 || adapter.Address.ToString() != lastAddress)
            {
                logger.LogInformation("Wi-Fi {Index}, {Address}/{Prefix}", adapter.Index, adapter.Address, adapter.Prefix);
                lastAdapter = adapter.Id;
                lastAddress = adapter.Address.ToString();
                force = true;
            }
            if (route != lastRoute)
            {
                logger.LogInformation("{Route}", route);
                lastRoute = route;
            }
            NetworkPrinter? networkPrinter = current is null || force ? null : await discovery.CheckAddressAsync(adapter, current, token);
            NetworkPrinter? candidate = networkPrinter;
            if (candidate == null || !candidate.Raw || !identities.Matches(profile.Identity, candidate))
            {
                candidate = null;
                if (settings.AutoDiscover | force)
                {
                    bool scan = DateTimeOffset.UtcNow >= nextScan;
                    if (scan)
                    {
                        nextScan = DateTimeOffset.UtcNow.AddMinutes(2.0);
                    }
                    IEnumerable<string> hints = new string?[3]
                    {
                        profile.Identity.LastAddress,
                        profile.Identity.Host,
                        queue?.Host
                    }.OfType<string>();
                    IReadOnlyList<NetworkPrinter> found = await discovery.FindAsync(adapter, hints, scan, token);
                    candidate = identities.Resolve(profile.Identity, found);
                }
            }
            if (candidate == null)
            {
                if (previouslyAvailable)
                {
                    logger.LogWarning("The printer stopped responding with its confirmed identity.");
                }
                previouslyAvailable = false;
                Update(new MonitorState(T("The confirmed printer is unavailable. A VPN or firewall may be blocking local TCP."), Searching: false, Available: false, adapter, null, queue, route));
                return;
            }
            if (!candidate.Address.Equals(current))
            {
                logger.LogInformation("Printer IP changed from {Previous} to {Current}", current, candidate.Address);
                profile = profile with
                {
                    Identity = profile.Identity with
                    {
                        LastAddress = candidate.Address.ToString()
                    }
                };
                storage.Save(settings with
                {
                    Profile = profile
                });
            }
            bridge.SetTarget(new BridgeTarget(adapter, candidate, profile.Identity));
            LastSuccess = DateTimeOffset.Now;
            if (!previouslyAvailable)
            {
                logger.LogInformation("Confirmed printer is reachable at {Address}:9100", candidate.Address);
            }
            previouslyAvailable = true;
            Update(new MonitorState(T("RAW TCP/9100 is reachable. The Windows queue is connected."), Searching: false, Available: true, adapter, candidate, queue, routing.Describe(adapter, candidate.Address)));
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            bridge.SetTarget(null);
            logger.LogError(ex, "Connection check failed");
            Update(new MonitorState(ex.Message, Searching: false, Available: false, adapter, null, queue, route));
        }
    }

    public void EnsureListener(AppSettings settings, int port)
    {
        if (!bridge.Running)
        {
            bridge.Start(port, settings.MaxConnections, TimeSpan.FromSeconds(settings.IdleSeconds), (BridgeTarget target, CancellationToken ct) => ConnectConfirmedAsync(target, settings.ConnectSeconds, ct), (Exception ex) =>
            {
                logger.LogWarning(ex, "Bridge connection failed");
            });
            logger.LogInformation("Bridge listening on 127.0.0.1:{Port}", port);
        }
    }

    public async Task ConfirmPrinterAsync(WifiAdapter adapter, NetworkPrinter printer, PrinterIdentity identity, int timeout, CancellationToken token)
    {
        using (await ConnectConfirmedAsync(new BridgeTarget(adapter, printer, identity), timeout, token).ConfigureAwait(continueOnCapturedContext: false))
        {
        }
    }

    private async Task<TcpClient> ConnectConfirmedAsync(BridgeTarget target, int timeout, CancellationToken token)
    {
        const int attempts = 6;
        for (int attempt = 1; ; attempt++)
        {
            TcpClient? socket = null;
            try
            {
                socket = await RoutingDiagnosticsService.ConnectAsync(target.Adapter, target.Printer.Address, 9100, TimeSpan.FromSeconds(timeout), token).ConfigureAwait(continueOnCapturedContext: false);
                NetworkPrinter observed = await discovery.VerifyAddressAsync(target.Adapter, target.Printer.Address, target.Identity, token).ConfigureAwait(continueOnCapturedContext: false);
                if (!identities.Matches(target.Identity, observed))
                {
                    throw new InvalidOperationException(T("The printer answered on TCP/9100, but its identity could not be verified. Search again."));
                }
                TcpClient result = socket;
                socket = null;
                return result;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && attempt < attempts &&
                                       ex is SocketException or InvalidOperationException)
            {
                logger.LogDebug(ex, "Printer verification retry after attempt {Attempt}", attempt);
            }
            finally
            {
                socket?.Dispose();
            }
            await Task.Delay(TimeSpan.FromSeconds(2L), token).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}
