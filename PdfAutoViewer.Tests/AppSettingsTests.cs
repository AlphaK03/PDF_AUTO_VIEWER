using System.Text.Json;
using PdfAutoViewer.Core;
using Xunit;

namespace PdfAutoViewer.Tests;

/// <summary>
/// Verifies the default value and serialization of the configuration.
/// The preferred language is the only persisted option; these tests guarantee
/// that its default value is stable and that it survives a save/load round-trip.
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void PreferredLanguage_ByDefault_IsSPA()
        => Assert.Equal(LanguagePreference.SPA, new AppSettings().PreferredLanguage);

    [Theory]
    [InlineData(LanguagePreference.Any)]
    [InlineData(LanguagePreference.SPA)]
    [InlineData(LanguagePreference.ENG)]
    public void Serialization_PreservesPreferredLanguage(LanguagePreference language)
    {
        var original = new AppSettings { PreferredLanguage = language };

        var json     = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(language, restored.PreferredLanguage);
    }

    [Fact]
    public void EffectiveWatchFolder_ReturnsNonEmptyPath()
        => Assert.False(string.IsNullOrWhiteSpace(new AppSettings().EffectiveWatchFolder));
}
