using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBridge;

public sealed class NetworkAdapterService
{
    public IReadOnlyList<WifiAdapter> GetAdapters()
    {
        Dictionary<Guid, string?> wlan = ConnectedWlan();
        return (from adapter in (from nic in NetworkInterface.GetAllNetworkInterfaces()
                where nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && nic.OperationalStatus == OperationalStatus.Up && Guid.TryParse(nic.Id, out var result) && wlan.ContainsKey(result) && !nic.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                select nic).SelectMany((NetworkInterface nic) =>
            {
                IPInterfaceProperties properties = nic.GetIPProperties();
                Guid id = Guid.Parse(nic.Id);
                int index = properties.GetIPv4Properties().Index;
                return (!RoutingDiagnosticsService.IsHardware(index)) ? Enumerable.Empty<WifiAdapter>() : (from ip in properties.UnicastAddresses
                    where ip.Address.AddressFamily == AddressFamily.InterNetwork && ip.PrefixLength > 0 && !((ReadOnlySpan<byte>)ip.Address.GetAddressBytes().AsSpan()).StartsWith((ReadOnlySpan<byte>)new byte[2] { 169, 254 })
                    select new WifiAdapter(id, nic.Name, nic.Description, index, ip.Address, ip.PrefixLength, properties.GatewayAddresses.FirstOrDefault((GatewayIPAddressInformation g) => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString(), wlan[id], RoutingDiagnosticsService.GetMetric(index)));
            })
            orderby adapter.Gateway != null descending, adapter.Metric, adapter.Index
            select adapter).ToArray();
    }

    private static Dictionary<Guid, string?> ConnectedWlan()
    {
        Check(WlanOpenHandle(2, IntPtr.Zero, out var _, out var handle));
        try
        {
            Check(WlanEnumInterfaces(handle, IntPtr.Zero, out var list));
            try
            {
                Dictionary<Guid, string?> result = new Dictionary<Guid, string?>();
                for (int i = 0; i < Marshal.ReadInt32(list); i++)
                {
                    nint item = list + 8 + 532 * i;
                    if (Marshal.ReadInt32(item, 528) == 1)
                    {
                        Guid id = Marshal.PtrToStructure<Guid>(item);
                        result[id] = ReadSsid(handle, id);
                    }
                }
                return result;
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static string? ReadSsid(nint handle, Guid id)
    {
        if (WlanQueryInterface(handle, ref id, 7, IntPtr.Zero, out var size, out var data, IntPtr.Zero) != 0)
        {
            return null;
        }
        try
        {
            if (size < 556)
            {
                return null;
            }
            int length = Marshal.ReadInt32(data, 520);
            if ((length < 1 || length > 32) ? true : false)
            {
                return null;
            }
            byte[] bytes = new byte[length];
            Marshal.Copy(data + 524, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private static void Check(int error)
    {
        if (error != 0)
        {
            throw new Win32Exception(error, "WLAN API: " + new Win32Exception(error).Message);
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(int version, nint reserved, out int negotiated, out nint handle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(nint handle, nint reserved, out nint list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(nint handle, ref Guid id, int opcode, nint reserved, out int size, out nint data, nint valueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint data);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(nint handle, nint reserved);
}

public sealed class RoutingDiagnosticsService
{
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    private struct IfRow
    {
        [FieldOffset(8)]
        public uint Index;

        [FieldOffset(1152)]
        public byte Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 168)]
    private struct InterfaceRow
    {
        [FieldOffset(0)]
        public ushort Family;

        [FieldOffset(16)]
        public uint Index;

        [FieldOffset(148)]
        public uint Metric;
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SockAddr
    {
        [FieldOffset(0)]
        public ushort Family;

        [FieldOffset(4)]
        public uint Address;

        public static SockAddr From(IPAddress address)
        {
            return new SockAddr
            {
                Family = 2,
                Address = BitConverter.ToUInt32(address.GetAddressBytes())
            };
        }
    }

    private enum NeighborState
    {
        Reachable = 5
    }

    private struct NeighborRow
    {
        public SockAddr Address;

        public uint InterfaceIndex;

        public ulong InterfaceLuid;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] PhysicalAddress;

        public uint PhysicalAddressLength;

        public NeighborState State;

        public byte Flags;

        public uint ReachabilityTime;
    }

    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private struct ForwardRow
    {
        [FieldOffset(8)]
        public uint Index;

        [FieldOffset(84)]
        public uint Metric;
    }

    public static void Pin(Socket socket, WifiAdapter adapter)
    {
        socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(adapter.Index));
        socket.Bind(new IPEndPoint(adapter.Address, 0));
    }

    public static async Task<TcpClient> ConnectAsync(WifiAdapter adapter, IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsCandidate(adapter, address))
        {
            throw new InvalidOperationException(T("The device address is outside the selected local network."));
        }
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        TcpClient client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            Pin(client.Client, adapter);
            await client.ConnectAsync(address, port, deadline.Token).ConfigureAwait(continueOnCapturedContext: false);
            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new SocketException(10060);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static bool IsCandidate(WifiAdapter adapter, IPAddress address)
    {
        if (!adapter.Contains(address) || IPAddress.IsLoopback(address) || address.Equals(adapter.Address))
        {
            return false;
        }
        uint value = WifiAdapter.AddressNumber(address);
        if (value >> 28 >= 14 || value >> 24 == 0)
        {
            return false;
        }
        if (adapter.Prefix < 31)
        {
            uint host = uint.MaxValue >> adapter.Prefix;
            if ((value & host) == 0 || (value & host) == host)
            {
                return false;
            }
        }
        return true;
    }

    public static string? Neighbor(WifiAdapter adapter, IPAddress address)
    {
        if (!IsCandidate(adapter, address))
        {
            return null;
        }
        NeighborRow row = new NeighborRow
        {
            Address = SockAddr.From(address),
            InterfaceIndex = (uint)adapter.Index,
            PhysicalAddress = new byte[32]
        };
        if (GetIpNetEntry2(ref row) != 0 || row.State != NeighborState.Reachable || row.PhysicalAddressLength != 6)
        {
            return null;
        }
        return Convert.ToHexString(row.PhysicalAddress.AsSpan(0, 6));
    }

    public string Describe(WifiAdapter? adapter, IPAddress? address)
    {
        IEnumerable<NetworkInterface> tunnels = NetworkInterface.GetAllNetworkInterfaces().Where((NetworkInterface nic) =>
        {
            bool flag = nic.OperationalStatus == OperationalStatus.Up;
            if (flag)
            {
                NetworkInterfaceType networkInterfaceType = nic.NetworkInterfaceType;
                bool flag2 = ((networkInterfaceType == NetworkInterfaceType.Ppp || networkInterfaceType == NetworkInterfaceType.Tunnel) ? true : false);
                flag = flag2 || new string[5] { "vpn", "wintun", "wireguard", "anyconnect", "tap-" }.Any((string term) => nic.Description.Contains(term, StringComparison.OrdinalIgnoreCase));
            }
            return flag;
        });
        string[] vpn = tunnels.Select(nic => nic.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lines = new List<string>();
        if (adapter == null || address == null)
        {
            lines.Add(T("Waiting for a printer address."));
        }
        else
        {
            SockAddr destination = SockAddr.From(address);
            SockAddr source = SockAddr.From(adapter.Address);
            int pinned = GetBestRoute2(IntPtr.Zero, (uint)adapter.Index, ref source, ref destination, 0u, out var best, out _);
            lines.Add(pinned == 0
                ? F("PrintBridge Wi-Fi route: interface {0}, metric {1}", best.Index, best.Metric)
                : F("PrintBridge Wi-Fi route: interface {0}, Windows error {1}", adapter.Index, pinned));
            int error = GetBestInterfaceEx(ref destination, out var index);
            lines.Add(error == 0
                ? F("Windows default route to the printer: interface {0}", index)
                : F("Windows default route lookup failed: error {0}", error));
        }
        lines.Add("");
        lines.Add(vpn.Length == 0 ? $"{T("Detected VPN interfaces")}: {T("none detected")}" : $"{T("Detected VPN interfaces")}:");
        lines.AddRange(vpn.Select(name => $"  • {name}"));
        return string.Join(Environment.NewLine, lines);
    }

    public static bool IsHardware(int index)
    {
        IfRow row = new IfRow
        {
            Index = (uint)index
        };
        if (GetIfEntry2(ref row) == 0)
        {
            return (row.Flags & 1) != 0;
        }
        return false;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfEntry2(ref IfRow row);

    public static uint GetMetric(int index)
    {
        InterfaceRow row = new InterfaceRow
        {
            Family = 2,
            Index = (uint)index
        };
        if (GetIpInterfaceEntry(ref row) != 0)
        {
            return uint.MaxValue;
        }
        return row.Metric;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestRoute2(nint luid, uint index, ref SockAddr source, ref SockAddr destination, uint options, out ForwardRow route, out SockAddr bestSource);

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetEntry2(ref NeighborRow row);

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterfaceEx(ref SockAddr destination, out uint index);

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpInterfaceEntry(ref InterfaceRow row);
}
