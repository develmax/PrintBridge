using System;
using System.IO;
using System.Text.Json;

namespace PrintBridge;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        WriteIndented = true
    };

    private readonly object gate = new object();

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintBridge");

    public static string LogFolder => Path.Combine(Folder, "Logs");

    public AppSettings Load()
    {
        var settings = Read<AppSettings>("settings.json") ?? new();
        var installed = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\PrintBridge", "InstallerLanguage", null) as string;
        return settings with { Language = Normalize(settings.Language ?? installed) };
    }

    public PrintPlan? Pending()
    {
        return Read<PrintPlan>("integration.json");
    }

    public void Save(AppSettings value)
    {
        Write("settings.json", value);
    }

    public void SavePending(PrintPlan value)
    {
        Write("integration.json", value);
    }

    public void ClearPending()
    {
        lock (gate)
        {
            File.Delete(Path.Combine(Folder, "integration.json"));
        }
    }

    private T? Read<T>(string name) where T : class
    {
        lock (gate)
        {
            string file = Path.Combine(Folder, name);
            return File.Exists(file) ? (JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) ?? throw new InvalidDataException(F("The file {0} contains no settings.", name))) : null;
        }
    }

    private void Write<T>(string name, T value)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Folder);
            string file = Path.Combine(Folder, name);
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, value, Json);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, file, overwrite: true);
            }
            finally
            {
                File.Delete(temp);
            }
        }
    }
}
