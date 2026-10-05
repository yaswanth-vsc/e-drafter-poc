using EDrafter.Api.Data;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EDrafter.Api.Services;

/// <summary>
/// Generates the rental agreement PDF from the form data.
///
/// Two things worth knowing about the signature blocks:
///
/// eDrafter's API places its cryptographic signature on ONE page, in ONE of four
/// corners — signaturePage and signaturePosition are top-level fields, not per
/// signatory. Since we generate the document ourselves, we draw a visible signature
/// block on EVERY page so the document reads as signed throughout. Those drawn blocks
/// are cosmetic; the binding e-signature lands on the last page only, and the footer
/// says so rather than implying otherwise.
///
/// No Aadhaar anywhere — the field was dropped by decision.
///
/// ZOHO LAYOUT (Agreement.SigningProvider == "zoho"): Zoho places a real signature box
/// for each party on every page, so instead of drawn signature blocks every page footer
/// keeps an empty band — first party bottom-left, second party bottom-right — sized by
/// SignatureLayout, which the Zoho fields use too. The eDrafter layout is left exactly as
/// it was proven.
/// </summary>
public sealed class AgreementPdfBuilder
{
    static AgreementPdfBuilder()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <param name="openingOnStamp">
    /// The opening (date, parties, recital) is printed on the e-stamp paper instead — see
    /// <see cref="BuildStampOpening"/> — so the body starts at the terms.
    /// </param>
    public byte[] Build(Agreement a, bool openingOnStamp = false)
    {
        var zoho = IsZohoLayout(a);

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);

                if (zoho)
                {
                    // Tight bottom margin: the footer itself holds the signature band, and
                    // its position must match SignatureLayout to the point.
                    page.MarginHorizontal(SignatureLayout.SideOffset, Unit.Point);
                    page.MarginTop(2, Unit.Centimetre);
                    page.MarginBottom(SignatureLayout.PageBottomMargin, Unit.Point);
                }
                else
                {
                    page.Margin(2, Unit.Centimetre);
                }

                // Lato ships embedded with QuestPDF, so the document renders identically
                // on any machine — including a Linux server with no fonts installed.
                page.DefaultTextStyle(t => t.FontSize(10).FontFamily("Lato"));

                page.Header().Element(h => Header(h, a));
                page.Content().Element(c => Content(c, a, openingOnStamp));
                if (zoho)
                    page.Footer().Element(f => ZohoFooter(f, a));
                else
                    page.Footer().Element(Footer);
            });
        });

        return doc.GeneratePdf();
    }

    private static void Header(IContainer c, Agreement a) =>
        c.Column(col =>
        {
            col.Item().AlignCenter().Text("RENTAL AGREEMENT")
                .FontSize(16).Bold().LetterSpacing(0.1f);
            col.Item().AlignCenter().Text("Residential Property — Term not exceeding 12 months")
                .FontSize(9).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(4).AlignCenter()
                .Text($"Karnataka · Article 30(1)(i) · Ref {a.RefId}")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });

    /// <summary>
    /// The opening of the agreement, laid into the blank area of the e-stamp paper under
    /// "Please write or type below this line" — the way agreements on stamp paper are
    /// normally written. A one-page PDF the size of the stamp page, transparent except for
    /// the text, overlaid onto the stamp; the stamp itself is never redrawn.
    ///
    /// ScaleToFit shrinks the block slightly if long names would overflow the area, so it
    /// can never run into the signature boxes below it.
    /// </summary>
    public byte[] BuildStampOpening(Agreement a, PdfPageSize stamp, StampTextArea area)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size((float)stamp.Width, (float)stamp.Height, Unit.Point);
                page.MarginLeft(area.Left, Unit.Point);
                page.MarginTop(area.Top, Unit.Point);
                page.MarginRight((float)stamp.Width - area.Left - area.Width, Unit.Point);
                page.MarginBottom((float)stamp.Height - area.Top - area.Height, Unit.Point);
                page.DefaultTextStyle(t => t.FontSize(9).FontFamily("Lato"));

                page.Content().ScaleToFit().Column(col =>
                {
                    col.Spacing(3);

                    col.Item().AlignCenter().Text("RENTAL AGREEMENT").FontSize(12).Bold().LetterSpacing(0.1f);
                    col.Item().AlignCenter()
                        .Text($"Residential Property · Karnataka · Article 30(1)(i) · Ref {a.RefId}")
                        .FontSize(7).FontColor(Colors.Grey.Darken2);

                    col.Item().PaddingTop(3).Text(t =>
                    {
                        t.Span("This Rental Agreement is made on ");
                        t.Span($"{DateTime.UtcNow:dd MMMM yyyy}").Bold();
                        t.Span(" between:");
                    });

                    col.Item().PaddingLeft(8).Text(t =>
                    {
                        t.Span("LESSOR (First Party): ").Bold();
                        t.Span(a.FirstPartyName).Bold();
                        t.Span($"   ·   {a.FirstPartyEmail}   ·   {a.FirstPartyPhone}").FontSize(8).FontColor(Colors.Grey.Darken2);
                    });
                    col.Item().PaddingLeft(8).Text(t =>
                    {
                        t.Span("LESSEE (Second Party): ").Bold();
                        t.Span(a.SecondPartyName).Bold();
                        t.Span($"   ·   {a.SecondPartyEmail}   ·   {a.SecondPartyPhone}").FontSize(8).FontColor(Colors.Grey.Darken2);
                    });

                    col.Item().PaddingTop(2).Text(
                        "WHEREAS the Lessor is the lawful owner of the property described in this Agreement and has " +
                        "agreed to lease it to the Lessee on the terms set out on the following page(s).");
                });
            });
        });

        return doc.GeneratePdf();
    }

    private static void Content(IContainer c, Agreement a, bool openingOnStamp) =>
        c.PaddingVertical(12).Column(col =>
        {
            col.Spacing(10);

            var start = a.LeaseStartDate;
            var end = start.AddMonths(a.LeaseTermMonths).AddDays(-1);

            if (openingOnStamp)
            {
                col.Item().Text("(continued from the e-stamp paper)")
                    .FontSize(8).Italic().FontColor(Colors.Grey.Darken1);
            }
            else
            {
                col.Item().Text(t =>
                {
                    t.Span("This Rental Agreement is made on ");
                    t.Span($"{DateTime.UtcNow:dd MMMM yyyy}").Bold();
                    t.Span(" between:");
                });

                // Parties
                col.Item().PaddingLeft(10).Column(p =>
                {
                    p.Spacing(6);
                    p.Item().Text(t =>
                    {
                        t.Span("LESSOR (First Party): ").Bold();
                        t.Span(a.FirstPartyName).Bold();
                    });
                    p.Item().PaddingLeft(10).Text($"Email: {a.FirstPartyEmail}   ·   Phone: {a.FirstPartyPhone}")
                        .FontSize(9).FontColor(Colors.Grey.Darken2);

                    p.Item().PaddingTop(4).Text(t =>
                    {
                        t.Span("LESSEE (Second Party): ").Bold();
                        t.Span(a.SecondPartyName).Bold();
                    });
                    p.Item().PaddingLeft(10).Text($"Email: {a.SecondPartyEmail}   ·   Phone: {a.SecondPartyPhone}")
                        .FontSize(9).FontColor(Colors.Grey.Darken2);
                });

                col.Item().PaddingTop(6).Text("WHEREAS the Lessor is the lawful owner of the property described " +
                                              "below and has agreed to lease it to the Lessee on the terms set out " +
                                              "in this Agreement:");
            }

            // Terms table
            col.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.ConstantColumn(150);
                    cd.RelativeColumn();
                });

                void Row(string label, string value)
                {
                    table.Cell().Element(LabelCell).Text(label).SemiBold();
                    table.Cell().Element(ValueCell).Text(value);
                }

                Row("Property", a.PropertyAddress);
                Row("Lease term", $"{a.LeaseTermMonths} months");
                Row("Commencing", start.ToString("dd MMMM yyyy"));
                Row("Ending", end.ToString("dd MMMM yyyy"));
                Row("Monthly rent", $"Rs. {a.MonthlyRent:N2}");
                Row("Consideration", $"Rs. {a.ConsiderationAmount:N2}");
                Row("Stamp duty", $"Rs. {a.Denomination:N2}  (0.5% of consideration, capped Rs. 500)");
                if (!string.IsNullOrWhiteSpace(a.CertificateNo))
                    Row("e-Stamp certificate", a.CertificateNo);
            });

            // Clauses
            col.Item().PaddingTop(10).Text("TERMS AND CONDITIONS").Bold().FontSize(11);

            var clauses = new[]
            {
                $"The Lessee shall pay a monthly rent of Rs. {a.MonthlyRent:N2}, payable in advance on or " +
                "before the fifth day of each calendar month.",

                $"This tenancy is for a term of {a.LeaseTermMonths} months commencing " +
                $"{start:dd MMMM yyyy} and ending {end:dd MMMM yyyy}, and shall not be construed as " +
                "creating any tenancy beyond that term.",

                "The Lessee shall use the property solely for residential purposes and shall not sublet, " +
                "assign or part with possession of the whole or any part of it without the prior written " +
                "consent of the Lessor.",

                "The Lessee shall maintain the property in good and tenantable condition, ordinary wear " +
                "and tear excepted, and shall permit the Lessor to inspect it at reasonable times on " +
                "reasonable notice.",

                "The Lessee shall pay all charges for electricity, water and other utilities consumed on " +
                "the property during the term of this lease.",

                "Either party may terminate this Agreement by giving one month's written notice to the " +
                "other party.",

                "Any dispute arising out of or in connection with this Agreement shall be subject to the " +
                "exclusive jurisdiction of the courts at Bengaluru, Karnataka."
            };

            for (var i = 0; i < clauses.Length; i++)
            {
                var n = i + 1;
                col.Item().Row(r =>
                {
                    r.ConstantItem(22).Text($"{n}.").SemiBold();
                    r.RelativeItem().Text(clauses[i]).LineHeight(1.3f);
                });
            }

            if (IsZohoLayout(a))
            {
                // No signature blocks here: both parties sign in the band at the foot of
                // EVERY page, the stamp paper included.
                // ShowEntire: never strand the tail of this sentence alone on a blank page.
                col.Item().PaddingTop(8).ShowEntire().Text(
                    "IN WITNESS WHEREOF the parties have executed this Agreement using Aadhaar eSign on the " +
                    "date first written above, signing every page, the e-stamp paper included.");

                col.Item().PageBreak();
                col.Item().Element(c2 => LegalNotice(c2, a));
                return;
            }

            col.Item().PaddingTop(8).Text(
                "IN WITNESS WHEREOF the parties have executed this Agreement electronically on the date " +
                "first written above.");

            // Signature blocks, driven by who is ACTUALLY being sent for signature.
            // A party who is not a signatory gets a manual line instead of the
            // "signed electronically" caption — printing that under someone who never
            // received a signing link would misrepresent the document.
            var firstSigns = a.Signatories.Any(s =>
                string.Equals(s.Email, a.FirstPartyEmail, StringComparison.OrdinalIgnoreCase));
            var secondSigns = a.Signatories.Any(s =>
                string.Equals(s.Email, a.SecondPartyEmail, StringComparison.OrdinalIgnoreCase));

            col.Item().PaddingTop(16).Row(r =>
            {
                r.RelativeItem().Element(c2 =>
                    SignatureBlock(c2, "LESSOR", a.FirstPartyName, firstSigns));
                r.ConstantItem(40);
                r.RelativeItem().Element(c2 =>
                    SignatureBlock(c2, "LESSEE", a.SecondPartyName, secondSigns));
            });
        });

    private static void SignatureBlock(IContainer c, string role, string name, bool signsElectronically) =>
        c.Column(col =>
        {
            col.Item().Height(34);
            col.Item().LineHorizontal(1).LineColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(3).Text(name).SemiBold().FontSize(9);
            col.Item().Text(role).FontSize(8).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(2)
                .Text(signsElectronically
                    ? "Signed electronically via eDrafter"
                    : "Signature")
                .FontSize(7).Italic().FontColor(Colors.Grey.Darken1);
        });

    private static void Footer(IContainer c) =>
        c.Column(col =>
        {
            col.Item().PaddingTop(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
            col.Item().PaddingTop(3).Row(r =>
            {
                r.RelativeItem().Text(
                        "The binding electronic signature is affixed on the final page of this document.")
                    .FontSize(7).FontColor(Colors.Grey.Darken1);
                r.ConstantItem(90).AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
        });

    /// <summary>
    /// Closing legal notice on its own final page.
    ///
    /// ⚠️ SAMPLE WORDING FOR THE POC. It has not been reviewed by a lawyer. Have legal
    /// approve or replace this text before any real agreement is executed with it.
    /// </summary>
    private static void LegalNotice(IContainer c, Agreement a) =>
        c.Column(col =>
        {
            col.Spacing(8);

            col.Item().Text("LEGAL NOTICE AND DECLARATIONS").Bold().FontSize(11);

            col.Item().Text(
                "1.  EXECUTION BY ELECTRONIC SIGNATURE. This Agreement is executed by the parties using " +
                "Aadhaar-based electronic signature. Both parties agree that an electronic signature affixed " +
                "in this manner is a valid and legally enforceable signature under the Information Technology " +
                "Act, 2000, and that this Agreement shall not be denied legal effect, validity or " +
                "enforceability solely because it is in electronic form.").LineHeight(1.3f);

            col.Item().Text(
                "2.  STAMP DUTY. The stamp duty payable on this instrument has been paid by means of the " +
                "electronic stamp certificate forming the first page of this document, which is an integral " +
                "part of this Agreement. The parties acknowledge that the e-stamp certificate and this " +
                "Agreement together constitute a single instrument.").LineHeight(1.3f);

            col.Item().Text(
                "3.  AUTHENTICATION OF SIGNATORIES. Each party confirms that the name, mobile number and " +
                "email address recorded in this Agreement are their own, that the Aadhaar credentials used to " +
                "sign belong to them, and that they have signed of their own free will after reading and " +
                "understanding its contents.").LineHeight(1.3f);

            col.Item().Text(
                "4.  COUNTERPARTS AND COPIES. This Agreement is executed in a single electronic original. " +
                "Each party shall receive an identical electronic copy, and every such copy, together with " +
                "the completion certificate issued by the e-signature service provider, shall be treated as " +
                "an original for all purposes.").LineHeight(1.3f);

            col.Item().Text(
                "5.  ENTIRE AGREEMENT. This Agreement records the entire understanding between the parties " +
                "in respect of the said property and supersedes all prior discussions, representations and " +
                "arrangements, whether oral or written. No amendment shall be valid unless made in writing " +
                "and executed by both parties in the same manner as this Agreement.").LineHeight(1.3f);

            col.Item().Text(
                "6.  SEVERABILITY. If any provision of this Agreement is held to be invalid or unenforceable, " +
                "that provision shall be severed and the remaining provisions shall continue in full force " +
                "and effect.").LineHeight(1.3f);

            col.Item().PaddingTop(6).Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken1).LineHeight(1.3f));
                t.Span("Note: ").SemiBold();
                t.Span(
                    "this wording is a sample prepared for demonstration purposes and has not been reviewed " +
                    "by a legal practitioner. It must be approved or replaced by qualified legal advice " +
                    "before being relied upon.").Italic();
            });

            col.Item().PaddingTop(4).Text(
                    $"Reference {a.RefId}" +
                    (string.IsNullOrWhiteSpace(a.CertificateNo) ? "" : $"  ·  e-Stamp certificate {a.CertificateNo}"))
                .FontSize(8).FontColor(Colors.Grey.Darken1);
        });

    private static bool IsZohoLayout(Agreement a) =>
        string.Equals(a.SigningProvider, "zoho", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Page number, captions, the empty signature band, then empty space for Zoho's Aadhaar
    /// eSign text. Everything below the captions is left blank on purpose — Zoho draws into
    /// it. Heights match SignatureLayout exactly.
    /// </summary>
    private static void ZohoFooter(IContainer c, Agreement a) =>
        c.Column(col =>
        {
            col.Item().PaddingTop(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
            col.Item().PaddingTop(3).Row(r =>
            {
                r.RelativeItem().Text("Signed electronically by both parties on every page (Aadhaar eSign).")
                    .FontSize(7).FontColor(Colors.Grey.Darken1);
                r.ConstantItem(90).AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });

            // Captions ABOVE the boxes: Zoho prints its own text under them.
            col.Item().PaddingTop(4).Height(SignatureLayout.CaptionHeight).PaddingBottom(3).Row(r =>
            {
                r.ConstantItem(SignatureLayout.BoxWidth).AlignBottom()
                    .Text($"First Party (Lessor): {a.FirstPartyName}")
                    .FontSize(7).FontColor(Colors.Grey.Darken2).ClampLines(1);
                r.RelativeItem();
                r.ConstantItem(SignatureLayout.BoxWidth).AlignBottom()
                    .Text($"Second Party (Lessee): {a.SecondPartyName}")
                    .FontSize(7).FontColor(Colors.Grey.Darken2).ClampLines(1);
                r.ConstantItem(SignatureLayout.RightSlack);
            });

            col.Item().Height(SignatureLayout.BoxHeight);          // Zoho's signature boxes
            col.Item().Height(SignatureLayout.AadhaarTextSpace);   // Zoho's "Date / Aadhaar eSign by" lines
        });

    private static IContainer LabelCell(IContainer c) =>
        c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4).PaddingRight(8);

    private static IContainer ValueCell(IContainer c) =>
        c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4);
}
