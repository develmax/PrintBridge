using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PrintBridge;

internal static class UpdateInstaller
{
    public static async Task<int> InstallAsync(string package, bool msi, ILogger logger)
    {
        logger.LogInformation("Update: worker verifying {Package}", Path.GetFileName(package));
        await using var verified = await GitHubUpdateService.OpenVerifiedPackageAsync(
            package, Version.Parse(Program.Version), msi, CancellationToken.None).ConfigureAwait(false);
        string log = Path.Combine(Path.GetDirectoryName(package)!, "installation.log");
        var start = new ProcessStartInfo
        {
            FileName = msi ? Path.Combine(Environment.SystemDirectory, "msiexec.exe") : package,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        string[] arguments = msi
            ? ["/i", package, "/qn", "/norestart", "/L*v", log, "PB_AUTOUPDATE=1", "MSIRESTARTMANAGERCONTROL=Disable"]
            : ["/PBUPDATE", "/SP-", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCLOSEAPPLICATIONS", $"/LOG={log}"];
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the update installer.");
        logger.LogInformation("Started automatic update from {Package}", Path.GetFileName(package));
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
            logger.LogWarning("Automatic update installer exited with code {Code}. Installer log: {InstallerLog}", process.ExitCode, log);
        else
            logger.LogInformation("Update: installer completed successfully. Installer log: {InstallerLog}", log);
        return process.ExitCode;
    }
}
