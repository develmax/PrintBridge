using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace PrintBridge;

public static class IppProxy
{
    private const string IppContentType = "application/ipp";

    public static async Task<WebApplication> StartAsync(int port, Func<BridgeTarget?> getTarget,
        Func<BridgeTarget, CancellationToken, Task<Stream>> connect, ILogger logger, CancellationToken token)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = null;
            server.Limits.MaxConcurrentConnections = 8;
            server.Listen(IPAddress.Loopback, port, listener => listener.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();
        app.Run(context => RelayAsync(context, getTarget, connect, logger));
        try
        {
            await app.StartAsync(token).ConfigureAwait(false);
            return app;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RelayAsync(HttpContext context, Func<BridgeTarget?> getTarget,
        Func<BridgeTarget, CancellationToken, Task<Stream>> connect, ILogger logger)
    {
        var request = context.Request;
        var response = context.Response;
        if (!HttpMethods.IsPost(request.Method) ||
            !MediaTypeHeaderValue.TryParse(request.ContentType, out var media) || media.MediaType != IppContentType ||
            request.Headers.ContainsKey("Content-Encoding"))
        {
            response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }
        var target = getTarget();
        if (target?.Printer.VerifiedIppUri is not { Scheme: "ipp" } printer ||
            !IPAddress.TryParse(printer.Host, out var address) || !address.Equals(target.Printer.Address))
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        int localPort = context.Connection.LocalPort;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            var token = deadline.Token;
            var (attributes, originalLength) = await IppMessage.RewriteAsync(request.Body,
                uri => IsLocalPrinterUri(uri, localPort) ? AtPrinter(printer, uri.AbsolutePath, uri.Query) : null,
                token).ConfigureAwait(false);
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                MaxResponseHeadersLength = 16,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                ConnectCallback = async (_, ct) => await connect(target, ct).ConfigureAwait(false)
            };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var upstream = new HttpRequestMessage(HttpMethod.Post,
                new UriBuilder("http", printer.Host, printer.Port)
                { Path = request.Path.ToUriComponent(), Query = request.QueryString.ToUriComponent() }.Uri);
            upstream.Content = new IppContent(attributes, request.Body, request.ContentLength - originalLength);
            upstream.Content.Headers.ContentType = new MediaTypeHeaderValue(IppContentType);
            using var result = await http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!result.IsSuccessStatusCode || result.Content.Headers.ContentType?.MediaType != IppContentType)
            {
                response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            await using var body = await result.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var (reply, _) = await IppMessage.RewriteAsync(body,
                uri => new UriBuilder("ipp", IPAddress.Loopback.ToString(), localPort)
                { Path = uri.AbsolutePath, Query = uri.Query }.Uri,
                token).ConfigureAwait(false);
            response.ContentType = IppContentType;
            await response.Body.WriteAsync(reply, token).ConfigureAwait(false);
            await body.CopyToAsync(response.Body, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(error, "IPP proxy request failed");
            if (response.HasStarted)
                context.Abort();
            else
                response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    private static bool IsLocalPrinterUri(Uri uri, int port) =>
        uri.Port == port && (uri.Host == "localhost" || uri.Host == IPAddress.Loopback.ToString());

    private static Uri AtPrinter(Uri printer, string path, string query) =>
        new UriBuilder(printer) { Path = path, Query = query }.Uri;

    private sealed class IppContent(byte[] attributes, Stream document, long? documentLength) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = attributes.Length + documentLength.GetValueOrDefault();
            return documentLength >= 0;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            await stream.WriteAsync(attributes, token).ConfigureAwait(false);
            await document.CopyToAsync(stream, token).ConfigureAwait(false);
        }
    }
}
