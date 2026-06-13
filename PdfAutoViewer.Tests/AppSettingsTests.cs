using System.Text.Json;
using PdfAutoViewer.Core;
using Xunit;

namespace PdfAutoViewer.Tests;

/// <summary>
/// Verifica el valor predeterminado y la serialización de la configuración.
/// El idioma preferido es la única opción persistida; estas pruebas garantizan
/// que su valor por defecto sea estable y que sobreviva un ciclo de
/// guardado/lectura.
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void PreferredLanguage_PorDefecto_EsSPA()
        => Assert.Equal(LanguagePreference.SPA, new AppSettings().PreferredLanguage);

    [Theory]
    [InlineData(LanguagePreference.Any)]
    [InlineData(LanguagePreference.SPA)]
    [InlineData(LanguagePreference.ENG)]
    public void Serializacion_PreservaElIdiomaPreferido(LanguagePreference idioma)
    {
        var original = new AppSettings { PreferredLanguage = idioma };

        var json     = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(idioma, restored.PreferredLanguage);
    }

    [Fact]
    public void EffectiveWatchFolder_DevuelveUnaRutaNoVacia()
        => Assert.False(string.IsNullOrWhiteSpace(new AppSettings().EffectiveWatchFolder));
}
