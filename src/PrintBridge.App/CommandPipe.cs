using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PrintBridge;

internal static class CommandPipe
{
    public static async Task ServeAsync(string session, Func<string[], Task<object>> execute, ILogger logger, CancellationToken stop)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        string name = session.Replace(@"Local\", "") + "." + process.SessionId + ".Command";
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                try
                {
                    using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    requestDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    char[] request = new char[8193];
                    int length = 0;
                    while (length < request.Length && await reader.ReadAsync(request.AsMemory(length, 1), requestDeadline.Token).ConfigureAwait(false) == 1 &&
                           request[length] != '\n')
                        length++;
                    var args = length < request.Length && request[length] == '\n'
                        ? JsonSerializer.Deserialize<string[]>(new string(request, 0, length)) : null;
                    if (args is not { Length: > 0 and <= 12 } || Array.Exists(args, string.IsNullOrWhiteSpace))
                        throw new ArgumentException("Invalid PrintBridge command.");
                    var result = await execute(args).ConfigureAwait(false);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, result })).ConfigureAwait(false);
                }
                catch (Exception error) when (!stop.IsCancellationRequested)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, error = error.Message })).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (IOException error)
            {
                logger.LogDebug(error, "PrintBridge command connection closed");
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }
}
