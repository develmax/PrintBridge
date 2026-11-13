using System.Globalization;
using System.Text.Json;

namespace PrintBridge;

public static class Localization
{
    public const string English = "en";
    public const string Russian = "ru";

    private static readonly Lazy<Dictionary<string, string>> Translations = new(() =>
    {
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream("PrintBridge.Localization.ru.json")
            ?? throw new InvalidOperationException("The Russian language resource is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    });

    public static string Normalize(string? language) => language == Russian ? Russian : English;

    private static bool configured;

    public static void Select(string? language)
    {
        var culture = CultureInfo.GetCultureInfo(Normalize(language));
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Volatile.Write(ref configured, true);
    }

    public static string T(string english) =>
        Volatile.Read(ref configured) && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Russian &&
        Translations.Value.TryGetValue(english, out var russian) ? russian : english;

    public static string F(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);
}
