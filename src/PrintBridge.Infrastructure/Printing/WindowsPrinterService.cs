using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PrintBridge;

public sealed class WindowsPrinterService : ISpooler
{
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    public SpoolerState Read()
    {
        return JsonSerializer.Deserialize<SpoolerState>(Run("$ports = @(Get-PrinterPort)\r\n$tcp = @{}\r\nforeach ($port in (Get-CimInstance Win32_TCPIPPrinterPort)) { $tcp[$port.Name] = $port }\r\n$portMap = @{}\r\n$portRows = @(foreach ($port in $ports) {\r\n  $portMap[$port.Name] = $port\r\n  $details = $tcp[$port.Name]\r\n  @{\r\n    Name = $port.Name; Host = $details.HostAddress; Number = [uint32]$details.PortNumber\r\n    Protocol = [uint32]$details.Protocol; SnmpEnabled = $details.SNMPEnabled; Monitor = $port.PortMonitor\r\n  }\r\n})\r\n$queues = @(foreach ($queue in (Get-Printer)) {\r\n  $details = $tcp[$queue.PortName]\n  $rawPort = $null\n  if ($null -ne $details) { $rawPort = [uint32]$details.Protocol -eq 1 }\r\n  @{\r\n    Name = $queue.Name; Driver = $queue.DriverName; Port = $queue.PortName\r\n    Host = $details.HostAddress; Monitor = $portMap[$queue.PortName].PortMonitor\r\n    Status = [string]$queue.PrinterStatus; Local = ($queue.Type -eq 'Local')\r\n    Shared = [bool]$queue.Shared; Comment = $queue.Comment\r\n    RawPort = $rawPort\r\n  }\r\n})\r\n@{ Queues = $queues; Ports = $portRows } | ConvertTo-Json -Depth 5 -Compress"), Json) ?? throw new InvalidOperationException(T("Windows returned no queue data."));
    }

    public void AddPort(PrintPlan plan)
    {
        Run("New-CimInstance -ClassName Win32_TCPIPPrinterPort -Property @{ Name = " + Literal(plan.PortName) + "; HostAddress = '127.0.0.1'; Protocol = [uint32]1; PortNumber = [uint32]" + plan.LocalPort + "; SNMPEnabled = $false; SNMPDevIndex = [uint32]0; SNMPCommunity = ''; Queue = ''; ByteCount = $false } | Out-Null");
    }

    public void DeletePort(string name)
    {
        Run("Remove-PrinterPort -InputObject (Find-Port " + Literal(name) + ")");
    }

    public void AddQueue(PrintPlan plan)
    {
        Run("Add-Printer -Name " + Literal(plan.Queue) + " -DriverName " + Literal(plan.Driver) + " -PortName " + Literal(plan.PortName) + " -Comment " + Literal(plan.Marker));
    }

    public void DeleteQueue(string name)
    {
        Run("Remove-Printer -InputObject (Find-Queue " + Literal(name) + ")");
    }

    public void SetPort(string queue, string port)
    {
        Run("Set-Printer -InputObject (Find-Queue " + Literal(queue) + ") -PortName " + Literal(port));
    }

    public bool HasJobs(string queue)
    {
        return Run("[string](@(Get-PrintJob -PrinterObject (Find-Queue " + Literal(queue) + ")).Count -gt 0)").Trim() == "True";
    }

    public void TestPage(string queue)
    {
        Run("$queue = Find-Queue ".TrimEnd() + " " + Literal(queue) + "\n$legacy = Get-CimInstance Win32_Printer | Where-Object Name -EQ $queue.Name\r\nif (@($legacy).Count -ne 1) { throw 'Print queue not found in Win32_Printer.' }\r\n$result = Invoke-CimMethod -InputObject $legacy -MethodName PrintTestPage\r\nif ($result.ReturnValue -ne 0) { throw \"PrintTestPage returned $($result.ReturnValue).\" }");
    }

    private static string Literal(string input)
    {
        return "([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(input)) + "')))";
    }

    private static string Run(string script)
    {
        string command = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'\r\n[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\r\nImport-Module PrintManagement -ErrorAction Stop\n$ProgressPreference = 'SilentlyContinue'\r\nfunction Find-Queue([string]$name) {\r\n  $queue = @(Get-Printer | Where-Object Name -EQ $name)\r\n  if ($queue.Count -ne 1) { throw 'Print queue missing or ambiguous.' }\r\n  $queue[0]\r\n}\r\nfunction Find-Port([string]$name) {\r\n  $port = @(Get-PrinterPort | Where-Object Name -EQ $name)\r\n  if ($port.Count -ne 1) { throw 'Printer port missing or ambiguous.' }\r\n  $port[0]\r\n}\ntry {\n" + script + "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }"));
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe"),
            Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        }) ?? throw new InvalidOperationException(T("Could not start Windows Print Management."));
        Task<string> task = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException(T("Windows Print Management timed out after 30 seconds."));
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Windows Print Management: " + errors.GetAwaiter().GetResult().Trim());
        }
        return task.GetAwaiter().GetResult();
    }
}
