using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

if (args.Length == 0 || args is ["help" or "--help" or "-h"])
{
    Console.WriteLine("""
        PrintBridge.Cli <command>

          status | adapters | queues | diagnostics | logs
          printers [adapter GUID]
          connect <queue> <IPv4> <separate|switch> [--adapter <GUID>] [--replace]
          restore | disconnect | check | start | stop | restart | test-page
          open-queue | printer-properties
          ipp-start <printer IPv4> <adapter GUID> | ipp-stop (experimental)

        Connect and restore may prompt for Windows administrator approval.
        Commands return JSON on success and write errors to stderr.
        """);
    return 0;
}

try
{
    string request = JsonSerializer.Serialize(args);
    if (request.Length > 8192)
        throw new ArgumentException("Command exceeds the size limit.");

    using var identity = WindowsIdentity.GetCurrent();
    using var current = Process.GetCurrentProcess();
    string instance = "PrintBridge." + identity.User!.Value;
    if (Mutex.TryOpenExisting(@"Local\" + instance, out var mutex))
        mutex.Dispose();
    else
    {
        using var host = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "PrintBridge.exe"),
            ArgumentList = { "--cli-host" },
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    using var pipe = new NamedPipeClientStream(".", $"{instance}.{current.SessionId}.Command",
        PipeDirection.InOut, PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
    try
    {
        pipe.Connect(10000);
    }
    catch (TimeoutException error)
    {
        throw new InvalidOperationException("Could not connect to PrintBridge. Make sure the installed app supports the command line and is running in this Windows session.", error);
    }
    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    using var reader = new StreamReader(pipe, leaveOpen: true);
    writer.WriteLine(request);
    using var response = JsonDocument.Parse(reader.ReadLine() ?? throw new IOException("PrintBridge closed the connection without a result."));
    if (!response.RootElement.GetProperty("ok").GetBoolean())
        throw new InvalidOperationException(response.RootElement.GetProperty("error").GetString());
    Console.WriteLine(JsonSerializer.Serialize(response.RootElement.GetProperty("result"), new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
