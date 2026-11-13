using System.Globalization;
using System.Text.Json;

namespace PrintBridge;

public static class Localization
{
    public const string English = "en";
    public const string Russian = "ru";

    public sealed record LanguageOption(string Code, string Name);

    public static IReadOnlyList<LanguageOption> Languages { get; } = Array.AsReadOnly<LanguageOption>(
    [
        new(English, "English"),
        new("ar", "العربية"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("nl", "Nederlands"),
        new("pl", "Polski"),
        new("pt-BR", "Português (Brasil)"),
        new("tr", "Türkçe"),
        new(Russian, "Русский"),
        new("sr", "Српски"),
        new("uk", "Українська"),
        new("zh-Hans", "简体中文"),
        new("ja", "日本語"),
        new("ko", "한국어")
    ]);

    private static readonly Dictionary<string, Lazy<Dictionary<string, string>>> Translations = Languages
        .Where(language => language.Code != English)
        .ToDictionary(language => language.Code, language => new Lazy<Dictionary<string, string>>(
            () => LoadCatalog(language.Code)), StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? language) =>
        Languages.FirstOrDefault(option => string.Equals(option.Code, language, StringComparison.OrdinalIgnoreCase))
            ?.Code ?? English;

    private static Dictionary<string, string> LoadCatalog(string language)
    {
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream($"PrintBridge.Localization.{language}.json")
            ?? throw new InvalidOperationException($"The language resource for {language} is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    private static bool configured;

    public static void Select(string? language)
    {
        var culture = CultureInfo.GetCultureInfo(Normalize(language));
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Volatile.Write(ref configured, true);
    }

    public static string T(string english) =>
        Volatile.Read(ref configured) && Translations.TryGetValue(CultureInfo.CurrentUICulture.Name, out var catalog) &&
        catalog.Value.TryGetValue(english, out var translation) ? translation : english;

    public static string F(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);
}
