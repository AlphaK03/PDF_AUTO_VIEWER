using PdfAutoViewer.Core;
using Xunit;

namespace PdfAutoViewer.Tests;

/// <summary>
/// Verifies the file-identity rules that govern three system behaviors:
///   • the language filter (_SPA / _ENG),
///   • the pairing of the two versions of the same document, and
///   • replacing a document with a more recent copy.
///
/// These are pure functions (no UI, no disk access), so the tests are
/// deterministic. Names follow the Method_Scenario_ExpectedResult convention.
/// </summary>
public class FileNamingTests
{
    // ── Language detection ───────────────────────────────────────────────

    [Theory]
    [InlineData("D000227828_H_SPA_MPI Masking Omniwire.pdf", "SPA")]
    [InlineData("D000227828_H_ENG MPI Masking Omniwire.pdf", "ENG")]
    [InlineData("report.pdf", "")]                  // no language suffix
    [InlineData("report_spa.pdf", "SPA")]           // case-insensitive
    [InlineData("report_SPANISH.pdf", "")]          // _SPA must end in _, space or end
    public void DetectLanguageSuffix_ReturnsExpectedLanguage(string file, string expected)
        => Assert.Equal(expected, PdfLifecycleManager.DetectLanguageSuffix(file));

    // ── Document pairing (language filter) ───────────────────────────────

    [Fact]
    public void GetPairingKey_SameDocumentDifferentLanguage_ProducesSameKey()
    {
        const string spa = @"C:\Downloads\D000227828_H_SPA_MPI Masking Omniwire.pdf";
        const string eng = @"C:\Downloads\D000227828_H_ENG MPI Masking Omniwire.pdf";

        Assert.Equal(PdfLifecycleManager.GetPairingKey(spa),
                     PdfLifecycleManager.GetPairingKey(eng));
    }

    [Fact]
    public void GetPairingKey_DifferentDocuments_ProduceDifferentKeys()
    {
        const string a = @"C:\Downloads\Report_A_SPA.pdf";
        const string b = @"C:\Downloads\Report_B_SPA.pdf";

        Assert.NotEqual(PdfLifecycleManager.GetPairingKey(a),
                        PdfLifecycleManager.GetPairingKey(b));
    }

    // ── Copy identity (replacement by a more recent copy) ────────────────

    [Theory]
    [InlineData(@"C:\D\report.pdf",   @"C:\D\report (1).pdf")]
    [InlineData(@"C:\D\doc_SPA.pdf",  @"C:\D\doc_SPA (2).pdf")]
    public void GetDocumentKey_DuplicateCopy_SharesKey(string original, string copy)
        => Assert.Equal(PdfLifecycleManager.GetDocumentKey(original),
                        PdfLifecycleManager.GetDocumentKey(copy));

    [Fact]
    public void GetDocumentKey_DifferentLanguage_DoesNotShareKey()
    {
        const string spa = @"C:\D\doc_SPA.pdf";
        const string eng = @"C:\D\doc_ENG.pdf";

        Assert.NotEqual(PdfLifecycleManager.GetDocumentKey(spa),
                        PdfLifecycleManager.GetDocumentKey(eng));
    }

    // ── Browser copy suffix ──────────────────────────────────────────────

    [Theory]
    [InlineData("report (2)", "report")]
    [InlineData("report", "report")]
    [InlineData("report (10)", "report")]
    [InlineData("v (2) final", "v (2) final")]      // only stripped at the end of the name
    public void StripNumericSuffix_RemovesCopySuffix(string stem, string expected)
        => Assert.Equal(expected, PdfLifecycleManager.StripNumericSuffix(stem));
}
