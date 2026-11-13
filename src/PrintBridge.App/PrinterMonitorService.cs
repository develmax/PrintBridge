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

public sealed record MonitorState(string Message, bool Searching = false, bool Available = false, LanAdapter? Adapter = null, NetworkPrinter? Printer = null, PrinterQueue? Queue = null, string? Route = null);

public sealed class PrinterMonitorService(SettingsService storage, NetworkAdapterService network, PrinterDiscoveryService discovery, PrinterIdentityService identities, WindowsPrinterService windows, RoutingDiagnosticsService routing, TcpBridgeService bridge, ILogger<PrinterMonitorService> logger)
{
    private DateTimeOffset nextScan;

    private Guid? lastAdapter;

    private string? lastAddress;

    private string? lastRoute;

    private bool previouslyAvailable;
    private LanAdapter[] printAdapters = [];

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
        LanAdapter? adapter = null;
        PrinterQueue? queue = null;
        string? route = null;
        try
        {
            var adaptersTask = Task.Run(network.GetAdapters, token);
            var spoolTask = Task.Run(windows.Read, token);
            await Task.WhenAll(adaptersTask, spoolTask);
            var spool = await spoolTask;
            queue = spool.Queue(profile.Print.Queue);
            if (!WindowsPrinterIntegrationService.Verify(profile.Print, spool))
            {
                bridge.SetTarget(null);
                Update(new MonitorState(T("The Windows queue or port is missing or has changed. Check the queue configuration."), Queue: queue));
                return;
            }

            IPAddress.TryParse(profile.Identity.LastAddress, out var current);
            var candidates = LanAdapter.OrderForPrinter(await adaptersTask, settings.Adapter, profile.LastAdapter, current);
            Volatile.Write(ref printAdapters, candidates);
            adapter = candidates.FirstOrDefault();
            if (adapter == null)
            {
                bridge.SetTarget(null);
                Update(new MonitorState(T("Waiting for the selected local network connection."), Queue: queue));
                previouslyAvailable = false;
                return;
            }

            EnsureListener(settings, profile.Print.LocalPort);
            var fallback = current == null ? null : candidates.FirstOrDefault(item => RoutingDiagnosticsService.IsCandidate(item, current));
            bridge.SetTarget(fallback == null ? null : new BridgeTarget(fallback,
                new NetworkPrinter(current!, profile.Identity.Host, profile.Identity.Model, null, null, null,
                    Raw: false, Ipp: false, Lpr: false, T("Saved address")), profile.Identity));

            NetworkPrinter? candidate = null;
            foreach (var item in candidates)
            {
                if (current == null || !RoutingDiagnosticsService.IsCandidate(item, current))
                    continue;
                var observed = await discovery.CheckAddressAsync(item, current, token);
                if (observed is { Raw: true } && identities.Matches(profile.Identity, observed))
                {
                    adapter = item;
                    candidate = observed;
                    break;
                }
            }

            bool adapterChanged = adapter.Id != lastAdapter || adapter.Address.ToString() != lastAddress;
            if ((candidate == null && (settings.AutoDiscover || force || adapterChanged)) || force)
            {
                bool scan = DateTimeOffset.UtcNow >= nextScan;
                if (scan)
                    nextScan = DateTimeOffset.UtcNow.AddMinutes(2);
                var hints = new[] { profile.Identity.LastAddress, profile.Identity.Host, queue?.Host }.OfType<string>().ToArray();
                foreach (var item in candidates)
                {
                    var observed = identities.Resolve(profile.Identity, await discovery.FindAsync(item, hints, scan, token));
                    if (observed == null)
                        continue;
                    adapter = item;
                    candidate = observed;
                    break;
                }
            }

            if (adapter.Id != lastAdapter || adapter.Address.ToString() != lastAddress)
            {
                logger.LogInformation("{ConnectionType} {Index}, {Address}/{Prefix}", adapter.ConnectionType, adapter.Index, adapter.Address, adapter.Prefix);
                lastAdapter = adapter.Id;
                lastAddress = adapter.Address.ToString();
            }
            route = routing.Describe(adapter, candidate?.Address ?? current);
            if (route != lastRoute)
            {
                logger.LogInformation("{Route}", route);
                lastRoute = route;
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
            if (!candidate.Address.Equals(current) || profile.LastAdapter != adapter.Id)
            {
                if (!candidate.Address.Equals(current))
                    logger.LogInformation("Printer IP changed from {Previous} to {Current}", current, candidate.Address);
                profile = profile with
                {
                    LastAdapter = adapter.Id,
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
            bridge.Start(port, settings.MaxConnections, TimeSpan.FromSeconds(settings.IdleSeconds), (BridgeTarget target, CancellationToken ct) =>
                ConnectConfirmedAsync(target, settings.ConnectSeconds, ct, Volatile.Read(ref printAdapters)), (Exception ex) =>
            {
                logger.LogWarning(ex, "Bridge connection failed");
            });
            logger.LogInformation("Bridge listening on 127.0.0.1:{Port}", port);
        }
    }

    public async Task ConfirmPrinterAsync(LanAdapter adapter, NetworkPrinter printer, PrinterIdentity identity, int timeout, CancellationToken token)
    {
        using (await ConnectConfirmedAsync(new BridgeTarget(adapter, printer, identity), timeout, token).ConfigureAwait(continueOnCapturedContext: false))
        {
        }
    }

    private async Task<TcpClient> ConnectConfirmedAsync(BridgeTarget target, int timeout, CancellationToken token,
        IReadOnlyList<LanAdapter>? alternatives = null)
    {
        const int attempts = 6;
        var address = target.Printer.Address;
        var paths = (alternatives ?? []).Where(adapter => RoutingDiagnosticsService.IsCandidate(adapter, address))
            .Prepend(target.Adapter).DistinctBy(adapter => (adapter.Id, adapter.Address)).ToArray();
        for (int attempt = 1; ; attempt++)
        {
            var adapter = paths[(attempt - 1) % paths.Length];
            TcpClient? socket = null;
            try
            {
                socket = await RoutingDiagnosticsService.ConnectAsync(adapter, address, 9100, TimeSpan.FromSeconds(timeout), token).ConfigureAwait(continueOnCapturedContext: false);
                NetworkPrinter observed = await discovery.VerifyAddressAsync(adapter, address, target.Identity, token).ConfigureAwait(continueOnCapturedContext: false);
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
