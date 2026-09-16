using EDrafter.Api.Data;

namespace EDrafter.Api.Services;

/// <summary>
/// Produces the document we send to eDrafter, and enforces the size limit before we
/// ever reach a spending call.
///
/// On merging: we do NOT merge the e-stamp ourselves. POST /esign accepts orderId +
/// stampId alongside documentBase64 and eDrafter merges the stamp onto the document
/// itself. Doing it twice would put two stamps on the page.
///
/// On size: documentBase64 is capped at roughly 700KB of base64, which is about 512KB
/// of actual PDF since base64 inflates by ~33%. There is no multipart upload endpoint.
/// Because we generate the PDF ourselves it comes out around 10-20KB, so this limit is
/// not a practical concern — but it is checked anyway, because exceeding it would fail
/// the call AFTER the wallet had been debited.
/// </summary>
public sealed class DocumentService(
    AgreementPdfBuilder pdfBuilder,
    IWebHostEnvironment env,
    ILogger<DocumentService> logger)
{
    /// <summary>~512KB of real PDF; base64 inflation takes it to ~700KB on the wire.</summary>
    public const int MaxPdfBytes = 512 * 1024;

    public DocumentResult BuildAgreementDocument(Agreement a)
    {
        var pdf = pdfBuilder.Build(a);

        if (pdf.Length > MaxPdfBytes)
        {
            logger.LogError(
                "Generated PDF is {Size} bytes, over the {Max} byte limit. Refusing to send.",
                pdf.Length, MaxPdfBytes);

            return new DocumentResult(
                Success: false,
                Bytes: null,
                Base64: null,
                Path: null,
                Error: $"Generated PDF is {pdf.Length / 1024}KB, over the ~{MaxPdfBytes / 1024}KB limit. " +
                       "This would fail at POST /esign AFTER the wallet was debited.");
        }

        var path = SaveToDisk(a, pdf);

        logger.LogInformation(
            "Agreement PDF for {Id}: {Size} bytes ({Pct:0.0}% of the limit)",
            a.Id, pdf.Length, 100.0 * pdf.Length / MaxPdfBytes);

        return new DocumentResult(true, pdf, Convert.ToBase64String(pdf), path, null);
    }

    private string SaveToDisk(Agreement a, byte[] pdf)
    {
        var dir = Path.Combine(env.ContentRootPath, "storage", "agreements");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{a.Id}.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    public string SaveStamp(Guid agreementId, byte[] pdf)
    {
        var dir = Path.Combine(env.ContentRootPath, "storage", "stamps");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{agreementId}.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    public string SaveSigned(Guid agreementId, byte[] pdf)
    {
        var dir = Path.Combine(env.ContentRootPath, "storage", "signed");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{agreementId}.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }
}

public sealed record DocumentResult(
    bool Success,
    byte[]? Bytes,
    string? Base64,
    string? Path,
    string? Error);
