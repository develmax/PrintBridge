using Microsoft.Win32;

namespace PrintBridge;

public sealed class StartupService
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue("PrintBridge") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (value)
                key.SetValue("PrintBridge", $"\"{Environment.ProcessPath}\" --tray");
            else
                key.DeleteValue("PrintBridge", throwOnMissingValue: false);
        }
    }
}
