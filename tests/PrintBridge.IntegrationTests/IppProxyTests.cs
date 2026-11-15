using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace PrintBridge.IntegrationTests;

public sealed class IppProxyTests
{
    [Fact]
    public async Task RelaysIppAttributesAndCopiesTheDocumentUnchanged()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        byte[] document = [0x25, 0x50, 0x44, 0x46, 0x03, 0x00, 0xff];
        var printer = new NetworkPrinter(IPAddress.Loopback, null, null, null, null, null, false, true, false, "test")
        {
            VerifiedIppUri = new Uri($"ipp://127.0.0.1:{upstreamPort}/ipp/print")
        };
        var adapter = new LanAdapter(Guid.NewGuid(), "test", "test", 0, IPAddress.Loopback, 8, null, null, 0);
        var target = new BridgeTarget(adapter, printer, PrinterIdentity.Confirm(printer));
        await using var proxy = await IppProxy.StartAsync(0, () => target, async (_, cancellation) =>
        {
            var socket = new TcpClient();
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, upstreamPort, cancellation);
                return new NetworkStream(socket.Client, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }, NullLogger.Instance, token);
        Uri loopback = new(proxy.Urls.Single());
        string localPrinter = $"ipp://127.0.0.1:{loopback.Port}/ipp/print";
        var server = ReplyAsync(upstream, printer.VerifiedIppUri, document, token);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var body = new ByteArrayContent(Message("printer-uri", localPrinter, document));
        body.Headers.ContentType = new("application/ipp");
        using var response = await client.PostAsync(new Uri(loopback, "/ipp/print"), body, token);
        await server;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = await response.Content.ReadAsByteArrayAsync(token);
        Assert.Contains($"ipp://127.0.0.1:{loopback.Port}/ipp/print/jobs/7", Encoding.ASCII.GetString(reply));
    }

    private static async Task ReplyAsync(TcpListener server, Uri printer, byte[] document, CancellationToken token)
    {
        using var client = await server.AcceptTcpClientAsync(token);
        using var stream = client.GetStream();
        using var headers = new MemoryStream();
        byte[] next = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(next, token);
            headers.Write(next);
            if (headers.Length >= 4 && headers.GetBuffer().AsSpan((int)headers.Length - 4, 4).SequenceEqual("\r\n\r\n"u8))
                break;
        }
        string headerText = Encoding.ASCII.GetString(headers.ToArray());
        Assert.StartsWith("POST /ipp/print ", headerText);
        string length = headerText.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        byte[] received = new byte[int.Parse(length.Split(':')[1])];
        await stream.ReadExactlyAsync(received, token);
        Assert.Equal(Message("printer-uri", printer.AbsoluteUri, document), received);

        byte[] response = Message("job-uri", printer.AbsoluteUri + "/jobs/7", []);
        byte[] responseHeaders = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/ipp\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(responseHeaders, token);
        await stream.WriteAsync(response, token);
    }

    private static byte[] Message(string attribute, string value, byte[] document)
    {
        byte[] name = Encoding.ASCII.GetBytes(attribute);
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        return [2, 0, 0, 0, 0, 0, 0, 1, 1, 0x45, 0, (byte)name.Length, .. name, 0, (byte)bytes.Length, .. bytes, 3, .. document];
    }
}
