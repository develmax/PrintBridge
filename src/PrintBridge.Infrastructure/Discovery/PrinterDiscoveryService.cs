using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBridge;

public sealed class PrinterDiscoveryService(ILogger<PrinterDiscoveryService> logger)
{
    public async Task<IReadOnlyList<NetworkPrinter>> FindAsync(LanAdapter adapter, IEnumerable<string> hints, bool scan, CancellationToken token)
    {
        Task<Advertisement[]> mdns = AdvertiseAsync(() => DiscoveryPackets.MdnsAsync(adapter, token), "mDNS", token);
        Task<Advertisement[]> wsd = AdvertiseAsync(() => DiscoveryPackets.WsdAsync(adapter, token), "WS-Discovery", token);
        Task<IEnumerable<IPAddress>> hosts = ResolveHintsAsync(adapter, hints, token);
        ILookup<IPAddress, Advertisement> advertisements = (await mdns.ConfigureAwait(continueOnCapturedContext: false)).Concat(await wsd.ConfigureAwait(continueOnCapturedContext: false)).ToLookup((Advertisement ad) => ad.Address);
        HashSet<IPAddress> known = (await hosts.ConfigureAwait(continueOnCapturedContext: false)).ToHashSet();
        IEnumerable<IPAddress> first = known.Concat(advertisements.Select((IGrouping<IPAddress, Advertisement> group) => group.Key));
        IEnumerable<IPAddress> second;
        if (!scan)
        {
            IEnumerable<IPAddress> enumerable = Array.Empty<IPAddress>();
            second = enumerable;
        }
        else
        {
            second = adapter.ScanAddresses(254);
        }
        IEnumerable<IPAddress> source = (from address in first.Concat(second).Distinct()
            where RoutingDiagnosticsService.IsCandidate(adapter, address)
            select address).Take(384);
        ConcurrentBag<NetworkPrinter> printers = new ConcurrentBag<NetworkPrinter>();
        await Parallel.ForEachAsync(source, new ParallelOptions
        {
            MaxDegreeOfParallelism = 20,
            CancellationToken = token
        }, async (IPAddress address, CancellationToken ct) =>
        {
            Advertisement[] ads = advertisements[address].ToArray();
            NetworkPrinter? printer = await InspectAsync(adapter, address, ads, known.Contains(address), ct).ConfigureAwait(continueOnCapturedContext: false);
            if (printer != null)
            {
                printers.Add(printer);
            }
        }).ConfigureAwait(continueOnCapturedContext: false);
        logger.LogDebug("Discovery on interface {Index}: {Count} candidates, mDNS {Mdns}, WSD {Wsd}", adapter.Index, printers.Count, mdns.Result.Length, wsd.Result.Length);
        return (from printer in printers
            orderby printer.Raw descending, LanAdapter.AddressNumber(printer.Address)
            select printer).ToArray();
    }

    public async Task<NetworkPrinter?> CheckAddressAsync(LanAdapter adapter, IPAddress address, CancellationToken token)
    {
        if (!RoutingDiagnosticsService.IsCandidate(adapter, address))
        {
            return null;
        }
        return await InspectAsync(adapter, address, Array.Empty<Advertisement>(), known: true, token).ConfigureAwait(continueOnCapturedContext: false);
    }

    public async Task<NetworkPrinter> VerifyAddressAsync(LanAdapter adapter, IPAddress address, PrinterIdentity identity, CancellationToken token)
    {
        string? mac = RoutingDiagnosticsService.Neighbor(adapter, address);
        if (identity.Mac != null && mac != null)
        {
            return new NetworkPrinter(address, null, null, mac, null, null, Raw: true, Ipp: false, Lpr: false, T("Network neighbor table"));
        }
        Advertisement[] matches = (await AdvertiseAsync(() => DiscoveryPackets.MdnsAsync(adapter, token), "mDNS", token).ConfigureAwait(continueOnCapturedContext: false)).Where((Advertisement ad) => ad.Address.Equals(address)).ToArray();
        Advertisement? ipp = await IppDiscovery.ProbeAsync(adapter, address, matches.Select((Advertisement ad) => ad.Resource).FirstOrDefault((string? resource) => resource != null), token).ConfigureAwait(continueOnCapturedContext: false);
        return new NetworkPrinter(address, null, ipp?.Model, mac, ipp?.Serial ?? Unique(matches.Select((Advertisement ad) => ad.Serial)), ipp?.Id ?? Unique(matches.Select((Advertisement ad) => ad.Id)), Raw: true, ipp != null, Lpr: false, (ipp == null) ? "DNS-SD" : "IPP");
    }

    private static async Task<NetworkPrinter?> InspectAsync(LanAdapter adapter, IPAddress address, Advertisement[] ads, bool known, CancellationToken token)
    {
        Task<bool> task = ProbePortAsync(adapter, address, 9100, known ? 1800 : 550, token);
        Task<bool> ipp = ProbePortAsync(adapter, address, 631, known ? 1300 : 500, token);
        bool rawOpen = await task.ConfigureAwait(continueOnCapturedContext: false);
        bool ippOpen = await ipp.ConfigureAwait(continueOnCapturedContext: false);
        if (!rawOpen && !ippOpen && ads.Length == 0 && !known)
        {
            return null;
        }
        Task<bool> lpr = ProbePortAsync(adapter, address, 515, 550, token);
        Advertisement? advertisement = ((!ippOpen) ? null : (await IppDiscovery.ProbeAsync(adapter, address, ads.Select((Advertisement a) => a.Resource).FirstOrDefault((string? r) => r != null), token).ConfigureAwait(continueOnCapturedContext: false)));
        Advertisement? details = advertisement;
        string? mac = (((rawOpen | ippOpen) || ads.Length != 0) ? RoutingDiagnosticsService.Neighbor(adapter, address) : null);
        string? model = details?.Model ?? ads.Select((Advertisement a) => a.Model).FirstOrDefault((string? m) => !string.IsNullOrWhiteSpace(m));
        string? uuid = details?.Id ?? Unique(from a in ads
            where a.Source == "DNS-SD"
            select a.Id);
        string? serial = details?.Serial ?? Unique(ads.Select((Advertisement a) => a.Serial));
        string? host = Unique(ads.Select((Advertisement a) => a.Host));
        if (host == null && (rawOpen | ippOpen))
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(500);
            try
            {
                host = (await Dns.GetHostEntryAsync(address).WaitAsync(deadline.Token).ConfigureAwait(continueOnCapturedContext: false)).HostName;
            }
            catch (SocketException)
            {
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
            }
        }
        List<string> sources = ads.Select((Advertisement a) => a.Source).Distinct().ToList();
        if (details != null)
        {
            sources.Add("IPP");
        }
        if (rawOpen)
        {
            sources.Add(ads.Any((Advertisement a) => a.Raw) ? T("RAW advertised") : "TCP/9100");
        }
        if (sources.Count == 0)
        {
            sources.Add(known ? T("Queue or profile address") : "TCP");
        }
        string? host2 = host;
        string? model2 = model;
        string? mac2 = mac;
        string? serial2 = serial;
        string? deviceId = uuid;
        bool raw = rawOpen;
        bool ipp2 = ippOpen || ads.Any((Advertisement ad) => ad.Ipp);
        return new NetworkPrinter(address, host2, model2, mac2, serial2, deviceId, raw, ipp2, await lpr.ConfigureAwait(continueOnCapturedContext: false) || ads.Any((Advertisement ad) => ad.Lpr), string.Join(", ", sources));
    }

    private static string? Unique(IEnumerable<string?> values)
    {
        string[] found = values.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(2)
            .ToArray();
        if (found.Length != 1)
        {
            return null;
        }
        return found[0];
    }

    private static async Task<bool> ProbePortAsync(LanAdapter adapter, IPAddress address, int port, int timeout, CancellationToken token)
    {
        try
        {
            using (await RoutingDiagnosticsService.ConnectAsync(adapter, address, port, TimeSpan.FromMilliseconds(timeout), token).ConfigureAwait(continueOnCapturedContext: false))
            {
                return true;
            }
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task<Advertisement[]> AdvertiseAsync(Func<Task<Advertisement[]>> discover, string protocol, CancellationToken token)
    {
        try
        {
            return await discover().ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (SocketException exception) when (!token.IsCancellationRequested)
        {
            logger.LogWarning(exception, "{Protocol} is unavailable on the selected interface", protocol);
            return Array.Empty<Advertisement>();
        }
    }

    private static async Task<IEnumerable<IPAddress>> ResolveHintsAsync(LanAdapter adapter, IEnumerable<string> hints, CancellationToken token)
    {
        CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            deadline.CancelAfter(1200);
            return from address in (await Task.WhenAll(hints.Where((string h) =>
                {
                    int length = h.Length;
                    return length > 0 && length <= 253;
                }).Distinct(StringComparer.OrdinalIgnoreCase).Take(32)
                    .Select(async (string hint) =>
                    {
                        if (IPAddress.TryParse(hint, out IPAddress? address))
                        {
                            return new IPAddress[1] { address };
                        }
                        if (Uri.CheckHostName(hint) != UriHostNameType.Dns)
                        {
                            return Array.Empty<IPAddress>();
                        }
                        try
                        {
                            return await Dns.GetHostAddressesAsync(hint, AddressFamily.InterNetwork, deadline.Token).ConfigureAwait(continueOnCapturedContext: false);
                        }
                        catch (SocketException)
                        {
                            return Array.Empty<IPAddress>();
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            return Array.Empty<IPAddress>();
                        }
                    })).ConfigureAwait(continueOnCapturedContext: false)).SelectMany((IPAddress[] addresses) => addresses)
                where RoutingDiagnosticsService.IsCandidate(adapter, address)
                select address;
        }
        finally
        {
            if (deadline != null)
            {
                ((IDisposable)deadline).Dispose();
            }
        }
    }
}
