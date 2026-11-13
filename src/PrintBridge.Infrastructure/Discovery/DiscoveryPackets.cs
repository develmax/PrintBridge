using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PrintBridge;

internal sealed record Advertisement(IPAddress Address, string? Host, string? Model, string? Id,
    string? Serial, bool Raw, bool Ipp, bool Lpr, string Source, string? Resource = null);

internal static class DiscoveryPackets
{
    private static readonly string[] Services =
        ["_ipp._tcp.local", "_ipps._tcp.local", "_printer._tcp.local", "_pdl-datastream._tcp.local"];

    public static async Task<Advertisement[]> MdnsAsync(WifiAdapter adapter, CancellationToken token)
    {
        var records = new List<DnsRecord>();
        await ReceiveAsync(adapter, "224.0.0.251", 5353, DnsQuery(), result =>
        {
            // Keep the whole response set because SRV, TXT and A can arrive separately.
            records.AddRange(ReadDns(result.Buffer));
        }, token).ConfigureAwait(false);

        var hosts = records.Where(record => record.Type == 1 && record.Data.Length == 4).ToLookup(record => record.Name);
        var texts = records.Where(record => record.Type == 16).ToLookup(record => record.Name);
        var instances = records.Where(record => record.Type == 12 && record.Target is not null)
            .ToLookup(record => record.Target!);
        return records.Where(record => record.Type == 33 && record.Target is not null)
            .SelectMany(srv => instances[srv.Name]
                .Where(ptr => Services.Contains(ptr.Name) && srv.Name.EndsWith("." + ptr.Name, StringComparison.Ordinal))
                .SelectMany(ptr => hosts[srv.Target!].Select(a =>
                {
                    var txt = texts[srv.Name].SelectMany(t => ReadTxt(t.Data))
                        .ToLookup(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
                    bool ipp = ptr.Name == Services[0] || ptr.Name == Services[1];
                    return new Advertisement(new IPAddress(a.Data), srv.Target, txt["ty"].FirstOrDefault() ?? txt["product"].FirstOrDefault(),
                        NormalizeId(txt["UUID"].FirstOrDefault()), NormalizeSerial(txt["serial"].FirstOrDefault()),
                        ptr.Name == Services[3] && srv.Port == 9100, ipp, ptr.Name == Services[2], "DNS-SD",
                        ptr.Name == Services[0] && srv.Port == 631 ? txt["rp"].FirstOrDefault() : null);
                }))).Distinct().ToArray();
    }

    public static async Task<Advertisement[]> WsdAsync(WifiAdapter adapter, CancellationToken token)
    {
        var results = new List<Advertisement>();
        string id = $"urn:uuid:{Guid.NewGuid()}";
        string probe = $"""
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <s:Header>
                <a:MessageID>{id}</a:MessageID>
                <a:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To>
                <a:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action>
              </s:Header>
              <s:Body><d:Probe/></s:Body>
            </s:Envelope>
            """;
        await ReceiveAsync(adapter, "239.255.255.250", 3702, Encoding.UTF8.GetBytes(probe), result =>
        {
            try
            {
                using var stream = new MemoryStream(result.Buffer, writable: false);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 65536 });
                var xml = XDocument.Load(reader);
                XNamespace addressing = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
                XNamespace discovery = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
                if (!xml.Descendants(addressing + "RelatesTo").Any(element => element.Value == id))
                    return;
                foreach (var match in xml.Descendants(discovery + "ProbeMatch"))
                {
                    if (!((string?)match.Element(discovery + "Types") ?? "").Contains("Print", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var device = (string?)match.Element(addressing + "EndpointReference")?.Element(addressing + "Address");
                    results.Add(new Advertisement(result.RemoteEndPoint.Address, null, null, NormalizeId(device),
                        null, false, false, false, "WS-Discovery"));
                }
            }
            catch (XmlException) { }
        }, token).ConfigureAwait(false);
        return results.Distinct().ToArray();
    }

    private static async Task ReceiveAsync(WifiAdapter adapter, string group, int port, byte[] message,
        Action<UdpReceiveResult> receive, CancellationToken token)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        RoutingDiagnosticsService.Pin(udp.Client, adapter);
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, adapter.Address.GetAddressBytes());
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try
        {
            await udp.SendAsync(message, new IPEndPoint(IPAddress.Parse(group), port), window.Token).ConfigureAwait(false);
            for (int i = 0; i < 128; i++)
            {
                var result = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                if (result.Buffer.Length <= 32768 && RoutingDiagnosticsService.IsCandidate(adapter, result.RemoteEndPoint.Address))
                    receive(result);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
    }

    private static byte[] DnsQuery()
    {
        using var stream = new MemoryStream();
        stream.Write([0, 0, 0, 0, 0, (byte)Services.Length, 0, 0, 0, 0, 0, 0]);
        foreach (string service in Services)
        {
            foreach (string label in service.Split('.'))
            {
                stream.WriteByte((byte)label.Length);
                stream.Write(Encoding.ASCII.GetBytes(label));
            }
            // PTR and QU (request unicast response). Legacy responders reply to the ephemeral source port.
            stream.Write([0, 0, 12, 128, 1]);
        }
        return stream.ToArray();
    }

    private sealed record DnsRecord(string Name, int Type, byte[] Data, string? Target, int Port);

    private static List<DnsRecord> ReadDns(byte[] packet)
    {
        var records = new List<DnsRecord>();
        try
        {
            int offset = 0;
            _ = Read16(packet, ref offset);
            int flags = Read16(packet, ref offset);
            if ((flags & 0x800F) != 0x8000)
                return records;
            int questions = Read16(packet, ref offset);
            int count = Read16(packet, ref offset) + Read16(packet, ref offset) + Read16(packet, ref offset);
            if (questions > 16 || count > 256)
                return records;
            for (int i = 0; i < questions; i++)
            {
                _ = ReadName(packet, ref offset);
                offset += 4;
            }
            for (int i = 0; i < count; i++)
            {
                string name = ReadName(packet, ref offset);
                int type = Read16(packet, ref offset);
                int cls = Read16(packet, ref offset);
                bool expired = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(offset, 4)) == 0;
                offset += 4;
                int length = Read16(packet, ref offset);
                int data = offset;
                offset += length;
                var payload = packet.AsSpan(data, length);
                if ((cls & 0x7FFF) != 1 || expired)
                    continue;
                if (type == 12)
                {
                    var target = ReadName(packet, ref data);
                    if (data == offset)
                        records.Add(new(name, type, [], target, 0));
                }
                else if (type == 33 && length >= 7)
                {
                    data += 4;
                    int port = Read16(packet, ref data);
                    var target = ReadName(packet, ref data);
                    if (data == offset)
                        records.Add(new(name, type, [], target, port));
                }
                else if (type is 1 or 16)
                    records.Add(new(name, type, payload.ToArray(), null, 0));
            }
            return records;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or FormatException)
        {
            return [];
        }
    }

    private static int Read16(byte[] packet, ref int offset)
    {
        int value = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
        offset += 2;
        return value;
    }

    private static string ReadName(byte[] packet, ref int offset)
    {
        var name = new StringBuilder();
        int position = offset;
        bool compressed = false;
        for (int i = 0; i < 128; i++)
        {
            int length = packet[position++];
            if (length == 0)
            {
                if (!compressed)
                    offset = position;
                return name.ToString().ToLowerInvariant();
            }
            if ((length & 0xc0) == 0xc0)
            {
                int pointer = ((length & 0x3f) << 8) | packet[position++];
                if (pointer >= position - 2)
                    throw new FormatException("Invalid DNS compression pointer.");
                if (!compressed)
                    offset = position;
                compressed = true;
                position = pointer;
                continue;
            }
            if (length > 63 || name.Length + length > 253)
                throw new FormatException("Invalid DNS label.");
            if (name.Length != 0)
                name.Append('.');
            name.Append(Encoding.UTF8.GetString(packet.AsSpan(position, length)));
            position += length;
        }
        throw new FormatException("Too many DNS labels.");
    }

    private static IEnumerable<KeyValuePair<string, string>> ReadTxt(byte[] bytes)
    {
        for (int offset = 0; offset < bytes.Length;)
        {
            int length = bytes[offset++];
            if (offset + length > bytes.Length)
                yield break;
            string value = Encoding.UTF8.GetString(bytes, offset, length);
            offset += length;
            int separator = value.IndexOf('=');
            if (separator > 0 && separator < value.Length - 1)
                yield return new(value[..separator], value[(separator + 1)..]);
        }
    }

    internal static string? NormalizeId(string? value) =>
        Guid.TryParse(value?.Replace("urn:uuid:", "", StringComparison.OrdinalIgnoreCase), out var id) && id != Guid.Empty
            ? id.ToString("D") : null;

    internal static string? NormalizeSerial(string? value) =>
        value?.Trim() is { Length: >= 4 and <= 80 } serial && serial.Any(char.IsLetterOrDigit) &&
        serial.Distinct().Count() > 1 && !new[] { "unknown", "none", "123456", "12345678", "123456789" }.Contains(serial, StringComparer.OrdinalIgnoreCase)
            ? serial : null;
}
