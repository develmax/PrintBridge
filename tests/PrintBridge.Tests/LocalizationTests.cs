using System.Globalization;

namespace PrintBridge.Tests;

public sealed class LocalizationTests
{
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
