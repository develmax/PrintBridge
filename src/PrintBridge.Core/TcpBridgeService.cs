using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace PrintBridge;

public sealed record BridgeTarget(WifiAdapter Adapter, NetworkPrinter Printer, PrinterIdentity Identity);

public sealed class TcpBridgeService : IAsyncDisposable, IDisposable
{
    private TcpListener? listener;

    private CancellationTokenSource? stop;

    private Task? acceptLoop;

    private BridgeTarget? target;

    private int active;

    private string? lastError;

    public bool Running => listener != null;

    public int Active => Volatile.Read(in active);

    public BridgeTarget? Target => Volatile.Read(in target);

    public string? LastError => Volatile.Read(in lastError);

    public void SetTarget(BridgeTarget? value)
    {
        Volatile.Write(ref target, value);
    }

    public void Start(int port, int maximum, TimeSpan idle, Func<BridgeTarget, CancellationToken, Task<TcpClient>> connect, Action<Exception>? onError = null)
    {
        if (Running)
        {
            throw new InvalidOperationException(T("The bridge is already running."));
        }
        TcpListener local = new TcpListener(IPAddress.Loopback, port)
        {
            ExclusiveAddressUse = true
        };
        local.Start(maximum);
        listener = local;
        stop = new CancellationTokenSource();
        lastError = null;
        acceptLoop = AcceptAsync(local, maximum, idle, connect, onError, stop.Token);
    }

    private async Task AcceptAsync(TcpListener local, int maximum, TimeSpan idle, Func<BridgeTarget, CancellationToken, Task<TcpClient>> connect, Action<Exception>? onError, CancellationToken token)
    {
        HashSet<Task> clients = new HashSet<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = await local.AcceptTcpClientAsync(token).ConfigureAwait(continueOnCapturedContext: false);
                clients.RemoveWhere((Task task) => task.IsCompleted);
                BridgeTarget? destination = Target;
                if (destination == null || clients.Count >= maximum)
                {
                    client.LingerState = new LingerOption(enable: true, 0);
                    client.Dispose();
                }
                else
                {
                    clients.Add(RelayAsync(client, destination, idle, connect, onError, token));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (SocketException exception)
        {
            Report(exception, onError);
        }
        finally
        {
            await Task.WhenAll(clients).ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    private async Task RelayAsync(TcpClient client, BridgeTarget destination, TimeSpan idle, Func<BridgeTarget, CancellationToken, Task<TcpClient>> connect, Action<Exception>? onError, CancellationToken token)
    {
        using (client)
        {
            Interlocked.Increment(ref active);
            string stage = T("Connecting to printer");
            try
            {
                using TcpClient remote = await connect(destination, token).ConfigureAwait(continueOnCapturedContext: false);
                stage = T("Opening TCP streams");
                NetworkStream localStream = client.GetStream();
                NetworkStream remoteStream = remote.GetStream();
                stage = T("Relaying data");
                using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(token);
                session.CancelAfter(idle);
                await Task.WhenAll(
                    PumpAsync(localStream, remoteStream, remote.Client, session, idle),
                    PumpAsync(remoteStream, localStream, client.Client, session, idle)).ConfigureAwait(false);
                Volatile.Write(ref lastError, null);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidOperationException)
            {
                try
                {
                    client.LingerState = new LingerOption(enable: true, 0);
                }
                catch (SocketException)
                {
                }
                if (!token.IsCancellationRequested)
                {
                    Report(new IOException(stage + ": " + ex.Message, ex), onError);
                }
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    private static async Task PumpAsync(NetworkStream source, NetworkStream destination, Socket output, CancellationTokenSource session, TimeSpan idle)
    {
        try
        {
            byte[] buffer = new byte[32768];
            int length;
            while ((length = await source.ReadAsync(buffer, session.Token).ConfigureAwait(continueOnCapturedContext: false)) != 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, length), session.Token).ConfigureAwait(continueOnCapturedContext: false);
                session.CancelAfter(idle);
            }
            output.Shutdown(SocketShutdown.Send);
        }
        catch
        {
            await session.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
            throw;
        }
    }

    private void Report(Exception exception, Action<Exception>? onError)
    {
        Volatile.Write(ref lastError, exception.Message);
        onError?.Invoke(exception);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (stop != null)
        {
            await stop.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
            await acceptLoop!.ConfigureAwait(continueOnCapturedContext: false);
            listener!.Stop();
            stop.Dispose();
            stop = null;
            listener = null;
            acceptLoop = null;
            SetTarget(null);
        }
    }
}
