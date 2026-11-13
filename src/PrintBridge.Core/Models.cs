using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace PrintBridge;

public sealed record LanAdapter(Guid Id, string Name, string Description, int Index, IPAddress Address, int Prefix, string? Gateway, string? Ssid, uint Metric, bool IsWireless = true)
{
    public string ConnectionType => IsWireless ? "Wi-Fi" : "Ethernet";

    public static LanAdapter[] OrderForPrinter(IEnumerable<LanAdapter> adapters, Guid? selected, Guid? preferred, IPAddress? address) =>
        adapters.Where(adapter => selected == null || adapter.Id == selected)
            .OrderByDescending(adapter => address != null && adapter.Contains(address))
            .ThenByDescending(adapter => adapter.Id == preferred)
            .ToArray();

    public override string ToString()
    {
        return $"{ConnectionType} · {Name} · {Ssid ?? Description} · {Address}/{Prefix}";
    }

    public bool Contains(IPAddress address)
    {
        bool flag = address.AddressFamily != AddressFamily.InterNetwork;
        if (!flag)
        {
            int prefix = Prefix;
            bool flag2 = ((prefix < 1 || prefix > 32) ? true : false);
            flag = flag2;
        }
        if (flag)
        {
            return false;
        }
        return AddressNumber(Address) >> 32 - Prefix == AddressNumber(address) >> 32 - Prefix;
    }

    public static uint AddressNumber(IPAddress ip)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
    }

    public IEnumerable<IPAddress> ScanAddresses(int limit)
    {
        int prefix = Math.Max(Prefix, 24);
        uint current = AddressNumber(Address);
        uint mask = (uint)(-1 << 32 - prefix);
        uint first = current & mask;
        uint last = first | ~mask;
        if (prefix < 31)
        {
            first++;
            last--;
        }
        for (ulong i = first; i <= last; i++)
        {
            if (limit <= 0)
            {
                break;
            }
            if (i != current)
            {
                limit--;
                yield return new IPAddress(new byte[4]
                {
                    (byte)(i >> 24),
                    (byte)(i >> 16),
                    (byte)(i >> 8),
                    (byte)i
                });
            }
        }
    }
}

public sealed record PrinterQueue(string Name, string Driver, string Port, string? Host, string? Monitor, string Status, bool Local, bool Shared, string? Comment, bool? RawPort)
{
    public override string ToString()
    {
        return $"{Name} · {Driver} · {Port}";
    }
}

public sealed record NetworkPrinter(IPAddress Address, string? Host, string? Model, string? Mac, string? Serial, string? DeviceId, bool Raw, bool Ipp, bool Lpr, string Source)
{
    public override string ToString()
    {
        return $"{Address} · {Model ?? Host ?? T("Unknown model")} · {Mac ?? Serial ?? T("No MAC or serial number")} · {(Raw ? "TCP/9100" : T("RAW unavailable"))} · {Source}";
    }
}

public sealed record PrinterIdentity(string? Mac, string? Serial, string? DeviceId, string? Host, string? Model, string LastAddress)
{
    public bool IsTrackable
    {
        get
        {
            if (Mac == null && Serial == null)
            {
                return DeviceId != null;
            }
            return true;
        }
    }

    public static PrinterIdentity Confirm(NetworkPrinter printer)
    {
        return new PrinterIdentity(printer.Mac, printer.Serial, printer.DeviceId, printer.Host, printer.Model, printer.Address.ToString());
    }
}

public sealed class PrinterIdentityService
{
    public bool Matches(PrinterIdentity saved, NetworkPrinter candidate)
    {
        if (Conflict(saved.Mac, candidate.Mac) || Conflict(saved.Serial, candidate.Serial) || Conflict(saved.DeviceId, candidate.DeviceId))
        {
            return false;
        }
        if (!Equal(saved.Mac, candidate.Mac) && !Equal(saved.Serial, candidate.Serial))
        {
            return Equal(saved.DeviceId, candidate.DeviceId);
        }
        return true;
        static bool Conflict(string? a, string? b)
        {
            if (a != null && b != null)
            {
                return !Equal(a, b);
            }
            return false;
        }
        static bool Equal(string? a, string? b)
        {
            if (a != null && b != null)
            {
                return a.Equals(b, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
    }

    public NetworkPrinter? Resolve(PrinterIdentity identity, IEnumerable<NetworkPrinter> candidates)
    {
        NetworkPrinter[] matches = candidates.Where((NetworkPrinter candidate) => Matches(identity, candidate)).Take(2).ToArray();
        if (matches.Length != 1 || !matches[0].Raw)
        {
            return null;
        }
        return matches[0];
    }

    public NetworkPrinter? Suggest(PrinterQueue queue, IEnumerable<NetworkPrinter> candidates)
    {
        NetworkPrinter[] match = candidates.Where((NetworkPrinter c) =>
        {
            if (c.Raw)
            {
                string? host = queue.Host;
                if (host != null && host.Length > 0)
                {
                    if (!host.Equals(c.Address.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        return host.TrimEnd('.').Equals(c.Host?.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
                    }
                    return true;
                }
            }
            return false;
        }).Take(2).ToArray();
        if (match.Length != 1)
        {
            return null;
        }
        return match[0];
    }
}

public sealed record PrintPlan(Guid Id, string SourceQueue, string Queue, string Driver, string OriginalPort, int LocalPort, bool Separate, bool ShortPortName = false)
{
    public string PortName => "PrintBridge_" + (ShortPortName ? Id.ToString("N").Substring(0, 6) : Id.ToString("N"));

    public string Marker => $"PrintBridge {Id:N}";

    public static PrintPlan Create(PrinterQueue source, int localPort, bool separate, SpoolerState spooler)
    {
        PrintPlan plan = new PrintPlan(Guid.NewGuid(), source.Name, separate ? (source.Name + " (PrintBridge)") : source.Name, source.Driver, source.Port, localPort, separate, ShortPortName: true);
        while (spooler.Port(plan.PortName) != null)
        {
            plan = plan with
            {
                Id = Guid.NewGuid()
            };
        }
        return plan;
    }
}

public sealed record Profile(PrintPlan Print, PrinterIdentity Identity, Guid? LastAdapter = null);

public sealed record AppSettings(bool StartHidden = true, bool Notify = true, bool AutoDiscover = true, int IntervalSeconds = 300, Guid? Adapter = null, int LocalPort = 19100, int ConnectSeconds = 4, int IdleSeconds = 120, int MaxConnections = 4, bool Verbose = false, Profile? Profile = null, string? Language = null, bool AutoUpdate = true, bool IdleIntervalConfigured = false);
