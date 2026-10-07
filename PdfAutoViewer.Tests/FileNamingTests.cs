using PdfAutoViewer.Core;
using Xunit;

namespace PdfAutoViewer.Tests;

/// <summary>
/// Verifies the file-identity rules the document selection relies on:
///   • language detection (_SPA / _ENG),
///   • type detection (".docx"-derived "_docx.pdf"), and
///   • the family key that groups every version of the same document.
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

    // ── Document family (every version of the same document) ─────────────

    [Theory]
    // language
    [InlineData(@"C:\D\D000227828_H_SPA_MPI Masking Omniwire.pdf", @"C:\D\D000227828_H_ENG MPI Masking Omniwire.pdf")]
    // type (real-world casing: the docx-derived one uses underscores)
    [InlineData(@"C:\D\D000227828_H_SPA_MPI Masking Omniwire.pdf", @"C:\D\D000227828_H_SPA_MPI_Masking_Omniwire_docx.pdf")]
    // REGRESSION: docx in one language vs native in the other — used to be
    // treated as two different documents, so both stayed open.
    [InlineData(@"C:\D\D000227828_H_SPA_MPI_Masking_Omniwire_docx.pdf", @"C:\D\D000227828_H_ENG MPI Masking Omniwire.pdf")]
    [InlineData(@"C:\D\D000227828_H_SPA_MPI Masking Omniwire.pdf", @"C:\D\D000227828_H_ENG_MPI_Masking_Omniwire_docx.pdf")]
    // browser copies
    [InlineData(@"C:\D\report.pdf", @"C:\D\report (1).pdf")]
    [InlineData(@"C:\D\D000227828_H_SPA_MPI_Masking_Omniwire_docx (1).pdf", @"C:\D\D000227828_H_ENG_MPI_Masking_Omniwire_docx.pdf")]
    // untagged vs tagged
    [InlineData(@"C:\D\Report_MPI.pdf", @"C:\D\Report_SPA_MPI.pdf")]
    public void GetFamilyKey_VersionsOfTheSameDocument_ShareKey(string a, string b)
        => Assert.Equal(PdfLifecycleManager.GetFamilyKey(a), PdfLifecycleManager.GetFamilyKey(b));

    [Theory]
    [InlineData(@"C:\D\Report_A_SPA.pdf", @"C:\D\Report_B_SPA.pdf")]       // different documents
    [InlineData(@"C:\D\Report_SPA.pdf",   @"C:\Other\Report_SPA.pdf")]     // different folders
    public void GetFamilyKey_DifferentDocuments_ProduceDifferentKeys(string a, string b)
        => Assert.NotEqual(PdfLifecycleManager.GetFamilyKey(a), PdfLifecycleManager.GetFamilyKey(b));

    // ── Browser copy suffix ──────────────────────────────────────────────

    [Theory]
    [InlineData("report (2)", "report")]
    [InlineData("report", "report")]
    [InlineData("report (10)", "report")]
    [InlineData("v (2) final", "v (2) final")]      // only stripped at the end of the name
    public void StripNumericSuffix_RemovesCopySuffix(string stem, string expected)
        => Assert.Equal(expected, PdfLifecycleManager.StripNumericSuffix(stem));

    // ── Document type (.docx-derived "_docx.pdf" vs native ".pdf") ───────

    [Theory]
    [InlineData("D000227828_H_SPA_MPI_Masking_Omniwire_docx.pdf", true)]
    [InlineData("D000227828_H_SPA_MPI Masking Omniwire.pdf", false)]
    [InlineData("report_DOCX.pdf", true)]           // case-insensitive
    [InlineData("report docx.pdf", true)]           // space separator
    [InlineData("reportdocx.pdf", false)]           // needs a separator before "docx"
    [InlineData("report_docx (1).pdf", true)]       // browser duplicate copy
    [InlineData("report_docx (2).pdf", true)]       // browser duplicate copy
    public void IsDocxType_DetectsDocxDerivedPdf(string file, bool expected)
        => Assert.Equal(expected, PdfLifecycleManager.IsDocxType(file));
}
