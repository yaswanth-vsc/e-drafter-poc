using EDrafter.Api.EDrafterClient;

namespace EDrafter.Api.Services;

/// <summary>
/// Where the two signatures go, on every page: first party bottom-left, second party
/// bottom-right.
///
/// These constants are shared by two things that must agree to the point:
///   - AgreementPdfBuilder, which leaves this exact band empty in every page footer, and
///   - the Zoho signature fields, which are placed into that band.
/// Change a number here and both move together.
///
/// All values are PDF points (1/72 inch), measured from the page edges.
///
/// SIZING, learned from the first live Aadhaar signature (2026-09-26):
///   - Zoho scales the signature to the box HEIGHT, keeping its proportions, so a long
///     cursive name comes out much wider than a tall box. A shorter box is the lever.
///   - The signature overflows to the RIGHT of the box, so the right box keeps a margin
///     of empty space before the page edge.
///   - Aadhaar eSign prints ~3 lines ("Date: …", "Aadhaar eSign by: <email>") directly
///     UNDER the box, the box's width wide. That space is left empty below every box.
///   - Captions therefore sit ABOVE the box, where Zoho draws nothing.
/// </summary>
public static class SignatureLayout
{
    /// <summary>2 cm — the agreement's left and right page margin, so boxes align with the text.</summary>
    public const float SideOffset = 56.69f;

    public const float BoxWidth = 210f;
    public const float BoxHeight = 30f;

    /// <summary>Empty space kept to the right of each box for a signature that runs wide.</summary>
    public const float RightSlack = 30f;

    /// <summary>Empty space kept under each box for Zoho's Aadhaar eSign date/email lines.</summary>
    public const float AadhaarTextSpace = 40f;

    /// <summary>The agreement page's bottom margin, below the Aadhaar text space.</summary>
    public const float PageBottomMargin = 20f;

    /// <summary>Gap between the page's bottom edge and the bottom of each signature box.</summary>
    public const float BottomOffset = PageBottomMargin + AadhaarTextSpace;

    /// <summary>The "First Party / Second Party" caption row ABOVE the boxes.</summary>
    public const float CaptionHeight = 12f;

    /// <summary>
    /// Defaults for the eDrafter stamp paper, measured on a real Karnataka e-stamp: its
    /// "Statutory Alert" text starts about 42pt above the bottom edge and a decorative
    /// border runs down both sides. The box and the Aadhaar text under it both sit in the
    /// blank area under "Please write or type below this line": box bottom 100pt up, text
    /// down to ~60pt, clear of the alert.
    /// </summary>
    public const float StampPageBottomOffset = 100f;
    public const float StampPageSideOffset = 65f;

    /// <summary>
    /// Top of the blank area on the stamp paper — just under the "Please write or type below
    /// this line" rule of the Karnataka e-stamp (measured at ~598pt on an 840pt page).
    /// </summary>
    public const float StampTextTop = 612f;

    /// <summary>Gap kept between the agreement text on the stamp and the signature boxes.</summary>
    public const float StampTextGapAboveBoxes = 6f;

    /// <summary>
    /// The stamp page's writable area: from <paramref name="top"/> down to just above the
    /// signature boxes, between the same side margins the boxes use.
    /// </summary>
    public static StampTextArea StampTextAreaFor(
        PdfPageSize stamp, float top = StampTextTop,
        float stampPageBottomOffset = StampPageBottomOffset, float stampPageSideOffset = StampPageSideOffset)
    {
        var boxTop = (float)stamp.Height - stampPageBottomOffset - BoxHeight;
        return new StampTextArea(
            Left: stampPageSideOffset,
            Top: top,
            Width: (float)stamp.Width - 2 * stampPageSideOffset,
            Height: Math.Max(boxTop - StampTextGapAboveBoxes - top, 0));
    }

    public const string FirstParty = "first";
    public const string SecondParty = "second";

    /// <summary>
    /// Two boxes per page. Stamp pages (the first <paramref name="stampPageCount"/>) use
    /// their own offsets, because the stamp paper is printed by the issuer, not by us.
    /// </summary>
    public static List<SignatureBox> Build(
        IReadOnlyList<PdfPageSize> pages, int stampPageCount,
        float stampPageBottomOffset = StampPageBottomOffset, float stampPageSideOffset = StampPageSideOffset)
    {
        var boxes = new List<SignatureBox>();

        for (var i = 0; i < pages.Count; i++)
        {
            var (w, h) = (pages[i].Width, pages[i].Height);
            var stamp = i < stampPageCount;
            var bottom = stamp ? stampPageBottomOffset : BottomOffset;
            var side = stamp ? stampPageSideOffset : SideOffset;
            var y = h - bottom - BoxHeight;

            boxes.Add(new SignatureBox(i, FirstParty, side, y, BoxWidth, BoxHeight, w, h));
            boxes.Add(new SignatureBox(i, SecondParty, w - side - RightSlack - BoxWidth, y, BoxWidth, BoxHeight, w, h));
        }

        return boxes;
    }

    /// <summary>
    /// The same boxes in the normalised shape SignaturePlacementInspector checks, so the
    /// Zoho layout gets the identical overlap and off-page checks the eDrafter one does.
    /// </summary>
    public static CreateEsignRequest ToInspectable(
        IReadOnlyList<SignatureBox> boxes, string firstPartyName, string secondPartyName)
    {
        EsignSignatoryDto For(string role, string name)
        {
            var mine = boxes.Where(b => b.Role == role).OrderBy(b => b.PageIndex).Select(b => new EsignPlaceholderDto
            {
                Page = b.PageIndex + 1,
                XNorm = (decimal)(b.X / b.PageWidth),
                YNorm = (decimal)(b.Y / b.PageHeight),
                WNorm = (decimal)(b.W / b.PageWidth),
                HNorm = (decimal)(b.H / b.PageHeight)
            }).ToList();

            return new EsignSignatoryDto
            {
                Name = name,
                Placeholder = mine.FirstOrDefault(),
                ExtraPlaceholders = mine.Skip(1).ToList()
            };
        }

        // Distinct labels even if both parties share a name, so the inspector reports an
        // overlap between them as an overlap rather than as one person's duplicate.
        return new CreateEsignRequest
        {
            Signatories =
            [
                For(FirstParty, $"First party ({firstPartyName})"),
                For(SecondParty, $"Second party ({secondPartyName})")
            ]
        };
    }
}

public sealed record PdfPageSize(double Width, double Height);

/// <summary>Where agreement text may go on the stamp page, in points from the top-left.</summary>
public sealed record StampTextArea(float Left, float Top, float Width, float Height);

/// <summary>One signature box in points, top-left origin, zero-based page index.</summary>
public sealed record SignatureBox(
    int PageIndex, string Role, double X, double Y, double W, double H, double PageWidth, double PageHeight);
