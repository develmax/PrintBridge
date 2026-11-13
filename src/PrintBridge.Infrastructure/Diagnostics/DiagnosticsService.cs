using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PrintBridge;

public sealed class DiagnosticsService : ILoggerProvider
{
    private readonly string logFolder;
    public DiagnosticsService() : this(SettingsService.LogFolder) { }
    public DiagnosticsService(string logFolder) => this.logFolder = logFolder;

    private readonly object gate = new();
    public bool Verbose { get; set; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    public void Clean()
    {
        lock (gate)
        {
            Directory.CreateDirectory(logFolder);
            foreach (string path in Directory.EnumerateFiles(logFolder, "printbridge.*.jsonl"))
                File.Delete(path);
        }
    }

    public static async Task ExportAsync(string destination, object report) =>
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

    private void Write<T>(string category, LogLevel level, EventId id, T state, Exception? error, Func<T, Exception?, string> format)
    {
        try
        {
            var properties = (state as IEnumerable<KeyValuePair<string, object?>>)?.ToDictionary(pair => pair.Key, pair => LogValue(pair.Value));
            string line = JsonSerializer.Serialize(new
            {
                Time = DateTimeOffset.UtcNow, Level = level.ToString(), Category = category, Event = id.Id,
                Message = format(state, error), Properties = properties, Error = error?.Message
            });
            lock (gate)
            {
                Directory.CreateDirectory(logFolder);
                string file = Path.Combine(logFolder, "printbridge.jsonl");
                if (File.Exists(file) && new FileInfo(file).Length >= 1024 * 1024)
                {
                    for (int index = 4; index >= 1; index--)
                    {
                        string previous = index == 1 ? file : Path.Combine(logFolder, $"printbridge.{index - 1}.jsonl");
                        if (File.Exists(previous))
                            File.Move(previous, Path.Combine(logFolder, $"printbridge.{index}.jsonl"), overwrite: true);
                    }
                }
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException or System.Text.Json.JsonException) { }
    }

    private static object? LogValue(object? value) => value switch
    {
        null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or decimal or Guid or DateTime or DateTimeOffset => value,
        float number when float.IsFinite(number) => number,
        double number when double.IsFinite(number) => number,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
    };

    private sealed class FileLogger(DiagnosticsService owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= (owner.Verbose ? LogLevel.Debug : LogLevel.Information) && level < LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
                owner.Write(category, level, eventId, state, exception, formatter);
        }
    }
}
