using EDrafter.Api.Data;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EDrafter.Api.Services;

/// <summary>
/// Generates the lease agreement PDF from the form data.
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
/// </summary>
public sealed class AgreementPdfBuilder
{
    static AgreementPdfBuilder()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] Build(Agreement a)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                // Lato ships embedded with QuestPDF, so the document renders identically
                // on any machine — including a Linux server with no fonts installed.
                page.DefaultTextStyle(t => t.FontSize(10).FontFamily("Lato"));

                page.Header().Element(h => Header(h, a));
                page.Content().Element(c => Content(c, a));
                page.Footer().Element(Footer);
            });
        });

        return doc.GeneratePdf();
    }

    private static void Header(IContainer c, Agreement a) =>
        c.Column(col =>
        {
            col.Item().AlignCenter().Text("LEASE AGREEMENT")
                .FontSize(16).Bold().LetterSpacing(0.1f);
            col.Item().AlignCenter().Text("Residential Property — Term not exceeding 12 months")
                .FontSize(9).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(4).AlignCenter()
                .Text($"Karnataka · Article 30(1)(i) · Ref {a.RefId}")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });

    private static void Content(IContainer c, Agreement a) =>
        c.PaddingVertical(12).Column(col =>
        {
            col.Spacing(10);

            var start = a.LeaseStartDate;
            var end = start.AddMonths(a.LeaseTermMonths).AddDays(-1);

            col.Item().Text(t =>
            {
                t.Span("This Lease Agreement is made on ");
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

                $"This lease is for a term of {a.LeaseTermMonths} months commencing " +
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

    private static IContainer LabelCell(IContainer c) =>
        c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4).PaddingRight(8);

    private static IContainer ValueCell(IContainer c) =>
        c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4);
}
