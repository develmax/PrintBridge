using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrintBridge.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void EverySupportedLanguageHasCompleteStringsAndPreservesFormatArguments()
    {
        var original = CultureInfo.CurrentUICulture;
        var defaultCulture = CultureInfo.DefaultThreadCurrentUICulture;
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream("PrintBridge.Localization.ru.json")!;
        var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!.Keys;
        try
        {
            foreach (var option in Localization.Languages)
            {
                Assert.Equal(option.Code, Localization.Normalize(option.Code.ToUpperInvariant()));
                Localization.Select(option.Code);
                Assert.Equal(option.Code, CultureInfo.CurrentUICulture.Name);
                if (option.Code == Localization.English)
                    continue;

                using var catalogStream = typeof(Localization).Assembly.GetManifestResourceStream(
                    $"PrintBridge.Localization.{option.Code}.json")!;
                var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(catalogStream)!;
                Assert.Equal(keys.Order(), catalog.Keys.Order());
                foreach (var key in keys)
                {
                    var text = Localization.T(key);
                    Assert.False(string.IsNullOrWhiteSpace(text), $"{option.Code}: {key}");
                    Assert.Equal(catalog[key], text);
                    Assert.Equal(Arguments(key), Arguments(text));
                    Assert.Equal(CompositeFormat.Parse(key).MinimumArgumentCount, CompositeFormat.Parse(text).MinimumArgumentCount);
                }
            }
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = defaultCulture;
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static IEnumerable<string> Arguments(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(match => match.Value).Order();

    [Fact]
    public void EnglishIsTheFallbackAndRussianUsesTheEmbeddedCatalog()
    {
        var original = CultureInfo.CurrentUICulture;
        var defaultCulture = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            Localization.Select(null);
            Assert.Equal("Settings", Localization.T("Settings"));
            Localization.Select("ru");
            Assert.Equal("Настройки", Localization.T("Settings"));
            Assert.Equal("Настройка PrintBridge 1.1.0", Localization.F("PrintBridge Setup {0}", "1.1.0"));
            Assert.Equal("Untranslated text", Localization.T("Untranslated text"));
            Localization.Select("unsupported");
            Assert.Equal("Settings", Localization.T("Settings"));
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = defaultCulture;
            CultureInfo.CurrentUICulture = original;
        }
    }
}
