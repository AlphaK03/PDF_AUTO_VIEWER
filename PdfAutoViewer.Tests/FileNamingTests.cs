using PdfAutoViewer.Core;
using Xunit;

namespace PdfAutoViewer.Tests;

/// <summary>
/// Verifica las reglas de identidad de archivos que gobiernan tres
/// comportamientos del sistema:
///   • el filtro de idioma (_SPA / _ENG),
///   • el emparejamiento de las dos versiones de un mismo documento, y
///   • el reemplazo de un documento por una copia más reciente.
///
/// Son funciones puras (sin interfaz ni acceso a disco), por lo que las
/// pruebas son deterministas. Los nombres siguen la convención
/// Método_Escenario_ResultadoEsperado.
/// </summary>
public class FileNamingTests
{
    // ── Detección de idioma ──────────────────────────────────────────────

    [Theory]
    [InlineData("D000227828_H_SPA_MPI Masking Omniwire.pdf", "SPA")]
    [InlineData("D000227828_H_ENG MPI Masking Omniwire.pdf", "ENG")]
    [InlineData("reporte.pdf", "")]                 // sin sufijo de idioma
    [InlineData("reporte_spa.pdf", "SPA")]          // no distingue mayúsculas
    [InlineData("reporte_SPANISH.pdf", "")]         // _SPA debe terminar en _, espacio o fin
    public void DetectLanguageSuffix_DevuelveElIdiomaEsperado(string file, string expected)
        => Assert.Equal(expected, PdfLifecycleManager.DetectLanguageSuffix(file));

    // ── Emparejamiento de documentos (filtro de idioma) ──────────────────

    [Fact]
    public void GetPairingKey_MismoDocumentoDistintoIdioma_ProduceLaMismaClave()
    {
        const string spa = @"C:\Downloads\D000227828_H_SPA_MPI Masking Omniwire.pdf";
        const string eng = @"C:\Downloads\D000227828_H_ENG MPI Masking Omniwire.pdf";

        Assert.Equal(PdfLifecycleManager.GetPairingKey(spa),
                     PdfLifecycleManager.GetPairingKey(eng));
    }

    [Fact]
    public void GetPairingKey_DocumentosDistintos_ProducenClavesDistintas()
    {
        const string a = @"C:\Downloads\Reporte_A_SPA.pdf";
        const string b = @"C:\Downloads\Reporte_B_SPA.pdf";

        Assert.NotEqual(PdfLifecycleManager.GetPairingKey(a),
                        PdfLifecycleManager.GetPairingKey(b));
    }

    // ── Identidad de copias (reemplazo por copia más reciente) ───────────

    [Theory]
    [InlineData(@"C:\D\reporte.pdf",  @"C:\D\reporte (1).pdf")]
    [InlineData(@"C:\D\doc_SPA.pdf",  @"C:\D\doc_SPA (2).pdf")]
    public void GetDocumentKey_CopiaDuplicada_ComparteClave(string original, string copia)
        => Assert.Equal(PdfLifecycleManager.GetDocumentKey(original),
                        PdfLifecycleManager.GetDocumentKey(copia));

    [Fact]
    public void GetDocumentKey_DistintoIdioma_NoComparteClave()
    {
        const string spa = @"C:\D\doc_SPA.pdf";
        const string eng = @"C:\D\doc_ENG.pdf";

        Assert.NotEqual(PdfLifecycleManager.GetDocumentKey(spa),
                        PdfLifecycleManager.GetDocumentKey(eng));
    }

    // ── Sufijo de copia del navegador ────────────────────────────────────

    [Theory]
    [InlineData("reporte (2)", "reporte")]
    [InlineData("reporte", "reporte")]
    [InlineData("reporte (10)", "reporte")]
    [InlineData("v (2) final", "v (2) final")]      // solo se quita al final del nombre
    public void StripNumericSuffix_QuitaElSufijoDeCopia(string stem, string expected)
        => Assert.Equal(expected, PdfLifecycleManager.StripNumericSuffix(stem));
}
