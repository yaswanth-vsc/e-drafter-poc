using EDrafter.Api.Data;
using PdfSharp.Pdf.IO;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EDrafter.Api.Services;

/// <summary>
/// Builds the document that goes to Zoho Sign: the eDrafter stamp paper, unchanged,
/// followed by the agreement.
///
/// The stamp is merged, never redrawn — it is the legal instrument as issued. eDrafter's
/// own e-sign merged it for us; Zoho knows nothing about the stamp, so here we do it.
///
/// Merge and overlay use QuestPDF's DocumentOperation (already a dependency). Page sizes
/// are read with PdfSharp, because the stamp paper need not be A4 and every signature box
/// is placed relative to its own page.
/// </summary>
public sealed class PdfComposer(IWebHostEnvironment env, ILogger<PdfComposer> logger)
{
    static PdfComposer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Merges stamp + agreement body and returns the file with its page geometry.
    /// <paramref name="stampOpening"/>, when given, is a one-page PDF laid over the FIRST
    /// stamp page — the start of the agreement, written in the stamp's blank area.
    /// </summary>
    public ComposedPdf Compose(Agreement a, byte[] agreementBody, byte[]? stampOpening = null)
    {
        if (a.StampPdfPath is null || !File.Exists(a.StampPdfPath))
            throw new InvalidOperationException(
                "The e-stamp PDF has not been downloaded yet, so there is nothing to put the agreement on.");

        var dir = Dir("final");
        var bodyPath = Path.Combine(dir, $"{a.Id}-body.pdf");
        var finalPath = Path.Combine(dir, $"{a.Id}.pdf");

        File.WriteAllBytes(bodyPath, agreementBody);

        // The stamp as issued stays untouched on disk; the written-on copy is separate.
        var stampPath = a.StampPdfPath;
        if (stampOpening is not null)
        {
            var openingPath = Path.Combine(dir, $"{a.Id}-stamp-opening.pdf");
            var writtenStampPath = Path.Combine(dir, $"{a.Id}-stamp-written.pdf");
            File.WriteAllBytes(openingPath, stampOpening);

            DocumentOperation
                .LoadFile(a.StampPdfPath)
                .OverlayFile(new DocumentOperation.LayerConfiguration { FilePath = openingPath, TargetPages = "1" })
                .Save(writtenStampPath);

            stampPath = writtenStampPath;
        }

        DocumentOperation
            .LoadFile(stampPath)
            .MergeFile(bodyPath)
            .Save(finalPath);

        var stampPages = ReadPageSizes(File.ReadAllBytes(a.StampPdfPath)).Count;
        var composed = Load(finalPath, stampPages);

        logger.LogInformation(
            "Final PDF for {Id}: {Pages} page(s) ({Stamp} stamp + {Body} agreement), {Size} bytes.",
            a.Id, composed.Pages.Count, stampPages, composed.Pages.Count - stampPages, composed.Bytes.Length);

        return composed;
    }

    /// <summary>Re-reads a final PDF already on disk — used once a Zoho draft holds that file.</summary>
    public ComposedPdf Load(string finalPath, int stampPageCount)
    {
        var bytes = File.ReadAllBytes(finalPath);
        return new ComposedPdf(bytes, finalPath, ReadPageSizes(bytes), stampPageCount);
    }

    /// <summary>
    /// The final PDF with every signature box drawn on it, labelled. Sends nothing — it
    /// exists so the layout can be checked by eye before a single Zoho call.
    /// </summary>
    public byte[] DrawPreview(ComposedPdf pdf, IReadOnlyList<SignatureBox> boxes, string firstName, string secondName)
    {
        var dir = Dir("final");
        var stem = Path.GetFileNameWithoutExtension(pdf.Path);
        var overlayPath = Path.Combine(dir, $"{stem}-overlay.pdf");
        var previewPath = Path.Combine(dir, $"{stem}-preview.pdf");

        Document.Create(doc =>
        {
            for (var i = 0; i < pdf.Pages.Count; i++)
            {
                var size = pdf.Pages[i];
                var onPage = boxes.Where(b => b.PageIndex == i).ToList();

                doc.Page(page =>
                {
                    page.Size((float)size.Width, (float)size.Height, Unit.Point);
                    page.Margin(0);
                    page.DefaultTextStyle(t => t.FontSize(7).FontFamily("Lato"));

                    page.Content().Layers(layers =>
                    {
                        layers.PrimaryLayer().Extend();

                        foreach (var b in onPage)
                        {
                            var first = b.Role == SignatureLayout.FirstParty;
                            var color = first ? Colors.Blue.Medium : Colors.Red.Medium;

                            // The space Zoho fills under the box with its Aadhaar eSign text.
                            layers.Layer()
                                .PaddingLeft((float)b.X)
                                .PaddingTop((float)(b.Y + b.H))
                                .AlignLeft().AlignTop()
                                .Width((float)b.W).Height(SignatureLayout.AadhaarTextSpace)
                                .Border(0.5f).BorderColor(Colors.Grey.Medium)
                                .AlignCenter().AlignMiddle()
                                .Text("Aadhaar eSign date / email").FontSize(6).FontColor(Colors.Grey.Medium);

                            layers.Layer()
                                .PaddingLeft((float)b.X)
                                .PaddingTop((float)b.Y)
                                .AlignLeft().AlignTop()
                                .Width((float)b.W).Height((float)b.H)
                                .Border(1.2f).BorderColor(color)
                                .AlignCenter().AlignMiddle()
                                .Text(first ? $"FIRST PARTY\n{firstName}" : $"SECOND PARTY\n{secondName}")
                                .FontColor(color).AlignCenter();
                        }
                    });
                });
            }
        }).GeneratePdf(overlayPath);

        DocumentOperation
            .LoadFile(pdf.Path)
            .OverlayFile(new DocumentOperation.LayerConfiguration { FilePath = overlayPath })
            .Save(previewPath);

        return File.ReadAllBytes(previewPath);
    }

    /// <summary>Width and height of every page, in points, with /Rotate applied.</summary>
    public static List<PdfPageSize> ReadPageSizes(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

        var sizes = new List<PdfPageSize>(doc.PageCount);
        foreach (var page in doc.Pages)
        {
            var w = page.Width.Point;
            var h = page.Height.Point;

            // A page rotated a quarter turn is displayed — and signed — sideways.
            sizes.Add(page.Rotate % 180 != 0 ? new PdfPageSize(h, w) : new PdfPageSize(w, h));
        }

        return sizes;
    }

    private string Dir(string name)
    {
        var dir = Path.Combine(env.ContentRootPath, "storage", name);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed record ComposedPdf(byte[] Bytes, string Path, IReadOnlyList<PdfPageSize> Pages, int StampPageCount);
