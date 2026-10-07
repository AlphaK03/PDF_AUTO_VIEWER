namespace PdfAutoViewer.Core;

public enum PdfErrorKind
{
    /// The built-in viewer (WebView2) could not start; the file was kept.
    ViewerFailed,

    /// Any other unexpected failure while processing the document.
    Unexpected,
}

/// <summary>
/// An error the operator must see: the document did not open. Raised by the
/// lifecycle manager and shown by the UI as a pop-up window.
/// </summary>
public sealed record PdfError(PdfErrorKind Kind, string DocumentName, string Detail, DateTime Time);
