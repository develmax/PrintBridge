using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace PrintBridge;

internal static class IppDiscovery
{
    public static async Task<Advertisement?> ProbeAsync(WifiAdapter adapter, IPAddress address, string? resource, CancellationToken token)
    {
        resource = (resource ?? "ipp/print").TrimStart('/');
        if (resource.Length is < 1 or > 200 || resource.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '/' and not '-' and not '_' and not '.'))
            return null;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            MaxResponseHeadersLength = 8,
            ConnectCallback = async (_, ct) =>
            {
                var tcp = await RoutingDiagnosticsService.ConnectAsync(adapter, address, 631, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                return tcp.GetStream();
            }
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{address}:631/{resource}");
        request.Content = new ByteArrayContent(BuildRequest($"ipp://{address}:631/{resource}"));
        request.Content.Headers.ContentType = new("application/ipp");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/ipp")
                return null;
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            byte[] data = new byte[32769];
            int size = 0;
            while (size < data.Length)
            {
                int count = await body.ReadAsync(data.AsMemory(size), deadline.Token).ConfigureAwait(false);
                if (count == 0)
                    return Parse(data.AsSpan(0, size), address);
                size += count;
            }
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return null; }
    }

    private static byte[] BuildRequest(string printerUri)
    {
        using var stream = new MemoryStream();
        // IPP Get-Printer-Attributes (RFC 8011).
        stream.Write([2, 0, 0, 11, 0, 0, 0, 1, 1]);
        void Attribute(byte tag, string name, string value)
        {
            stream.Write([tag, 0, (byte)name.Length]);
            stream.Write(Encoding.ASCII.GetBytes(name));
            var bytes = Encoding.ASCII.GetBytes(value);
            stream.Write([(byte)(bytes.Length >> 8), (byte)bytes.Length]);
            stream.Write(bytes);
        }
        Attribute(0x47, "attributes-charset", "utf-8");
        Attribute(0x48, "attributes-natural-language", "en");
        Attribute(0x45, "printer-uri", printerUri);
        Attribute(0x44, "requested-attributes", "printer-uuid");
        Attribute(0x44, "", "printer-make-and-model");
        Attribute(0x44, "", "printer-device-id");
        stream.WriteByte(3);
        return stream.ToArray();
    }

    private static Advertisement? Parse(ReadOnlySpan<byte> bytes, IPAddress address)
    {
        if (bytes.Length < 9 || bytes[0] is < 1 or > 2 || bytes[2] != 0 ||
            BinaryPrimitives.ReadInt32BigEndian(bytes[4..]) != 1)
            return null;
        string? model = null, id = null, serial = null;
        bool printerAttributes = false;
        for (int i = 8; i < bytes.Length;)
        {
            byte tag = bytes[i++];
            if (tag == 3)
                return new(address, null, model, id, serial, false, true, false, "IPP");
            if (tag < 16)
            {
                printerAttributes = tag == 4;
                continue;
            }
            if (!ReadString(bytes, ref i, out string name) || !ReadString(bytes, ref i, out string value))
                return null;
            if (!printerAttributes)
                continue;
            if (name == "printer-uuid")
                id = DiscoveryPackets.NormalizeId(value);
            if (name == "printer-make-and-model")
                model = value;
            if (name == "printer-device-id")
            {
                serial = value.Split(';').Select(field => field.Split(':', 2))
                    .Where(field => field.Length == 2 && field[0] is "SN" or "SERN" or "SERIALNUMBER")
                    .Select(field => DiscoveryPackets.NormalizeSerial(field[1])).FirstOrDefault();
            }
        }
        return null;
    }

    private static bool ReadString(ReadOnlySpan<byte> bytes, ref int position, out string value)
    {
        value = "";
        if (position + 2 > bytes.Length)
            return false;
        int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
        position += 2;
        if (position + length > bytes.Length)
            return false;
        value = Encoding.UTF8.GetString(bytes.Slice(position, length));
        position += length;
        return true;
    }
}
