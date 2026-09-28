using EDrafter.Api.EDrafterClient;

namespace EDrafter.Api.Services;

/// <summary>
/// Checks signature boxes for collisions BEFORE the document is sent.
///
/// Why this exists: eDrafter validates none of this. A request with two signatures stacked
/// on the same spot, or a placeholder for page 99 of a 5-page PDF, returns 201 exactly like
/// a correct one — the response echoes back neither the placeholders nor signAllPages. The
/// first sight of a mistake is the signed PDF, by which point the e-sign fee is spent and
/// non-refundable. Verified against the live API on 2026-09-22, where a request that set
/// both all-pages AND explicit placeholders produced two overlapping signatures per page.
///
/// So the only place overlap can be caught is here, before the call.
/// </summary>
public sealed class SignaturePlacementInspector
{
    /// <summary>
    /// Boxes closer than this on both axes are treated as touching. Signatures are drawn
    /// with a little padding, so boxes that merely abut still look collided.
    /// </summary>
    private const decimal Margin = 0.005m;

    public PlacementReport Inspect(CreateEsignRequest req, int pageCount)
    {
        var issues = new List<PlacementIssue>();

        // 1. All-pages AND explicit placeholders together: eDrafter applies both, so every
        //    such signatory is stamped twice per page. This is the exact defect seen in
        //    the 5-page test, and it is invisible in the response.
        foreach (var s in req.Signatories)
        {
            var allPages = req.SignAllPages == true || s.AllPages == true;
            var hasPlaceholders = s.ExtraPlaceholders is { Count: > 0 };

            if (hasPlaceholders && s.Placeholder is null)
            {
                issues.Add(new PlacementIssue(
                    "missing-base-placeholder", null, [s.Name],
                    $"{s.Name} has extraPlaceholders but no base placeholder. eDrafter " +
                    "ignores the coordinates in that case and drops the signatory — send a " +
                    "placeholder for the first page as well."));
            }

            if (allPages && hasPlaceholders)
            {
                issues.Add(new PlacementIssue(
                    "duplicate-placement", null, [s.Name],
                    $"{s.Name} has both all-pages and explicit placeholders. eDrafter applies " +
                    "both, so this signature will be stamped twice on every page. Use one or " +
                    "the other."));
            }
        }

        // 2. Placeholders pointing at pages the document does not have. eDrafter drops
        //    these silently, so the signature simply never appears.
        foreach (var s in req.Signatories)
        {
            foreach (var p in AllBoxes(s))
            {
                if (p.Page < 1 || p.Page > pageCount)
                {
                    issues.Add(new PlacementIssue(
                        "page-out-of-range", p.Page, [s.Name],
                        $"{s.Name} has a signature on page {p.Page}, but the document has " +
                        $"{pageCount} page(s). eDrafter will accept this and silently drop it."));
                }

                if (p.XNorm < 0 || p.YNorm < 0 || p.XNorm + p.WNorm > 1 || p.YNorm + p.HNorm > 1)
                {
                    issues.Add(new PlacementIssue(
                        "off-page", p.Page, [s.Name],
                        $"{s.Name}'s signature on page {p.Page} falls outside the page edges."));
                }
            }
        }

        // 3. The actual overlap check: every pair of boxes sharing a page.
        var boxes = req.Signatories
            .SelectMany(s => AllBoxes(s).Select(p => (Owner: s.Name, Box: p)))
            .ToList();

        foreach (var group in boxes.GroupBy(b => b.Box.Page))
        {
            var onPage = group.ToList();
            for (var i = 0; i < onPage.Count; i++)
            {
                for (var j = i + 1; j < onPage.Count; j++)
                {
                    if (!Intersects(onPage[i].Box, onPage[j].Box)) continue;

                    var a = onPage[i];
                    var b = onPage[j];

                    // Same signatory twice on one page is a duplicate; two different
                    // signatories on one spot is a collision. Different fixes, so name them
                    // differently.
                    var same = string.Equals(a.Owner, b.Owner, StringComparison.OrdinalIgnoreCase);

                    issues.Add(new PlacementIssue(
                        same ? "duplicate-placement" : "overlap",
                        group.Key,
                        same ? [a.Owner] : [a.Owner, b.Owner],
                        same
                            ? $"{a.Owner} has two signatures on the same spot on page {group.Key}."
                            : $"{a.Owner} and {b.Owner} overlap on page {group.Key}."));
                }
            }
        }

        return new PlacementReport(
            PageCount: pageCount,
            TotalBoxes: boxes.Count,
            Issues: issues,
            Pages: boxes
                .GroupBy(b => b.Box.Page)
                .OrderBy(g => g.Key)
                .Select(g => new PlacementPage(
                    g.Key,
                    g.Select(b => new PlacementBox(
                        b.Owner, b.Box.XNorm, b.Box.YNorm, b.Box.WNorm, b.Box.HNorm)).ToList()))
                .ToList());
    }

    /// <summary>Base placeholder and extras together — the full set eDrafter will draw.</summary>
    private static IEnumerable<EsignPlaceholderDto> AllBoxes(EsignSignatoryDto s)
    {
        if (s.Placeholder is not null) yield return s.Placeholder;
        foreach (var p in s.ExtraPlaceholders ?? []) yield return p;
    }

    /// <summary>
    /// Rectangle intersection, in the normalised top-left-origin space eDrafter uses.
    /// Boxes only touching at the margin count as clear.
    /// </summary>
    private static bool Intersects(EsignPlaceholderDto a, EsignPlaceholderDto b)
    {
        var apart =
            a.XNorm + a.WNorm <= b.XNorm + Margin ||
            b.XNorm + b.WNorm <= a.XNorm + Margin ||
            a.YNorm + a.HNorm <= b.YNorm + Margin ||
            b.YNorm + b.HNorm <= a.YNorm + Margin;

        return !apart;
    }
}

public sealed record PlacementReport(
    int PageCount,
    int TotalBoxes,
    List<PlacementIssue> Issues,
    List<PlacementPage> Pages)
{
    public bool HasOverlap => Issues.Count > 0;

    /// <summary>One line fit to show a user or write to a log.</summary>
    public string Summary => Issues.Count == 0
        ? $"{TotalBoxes} signature box(es) across {PageCount} page(s); no overlap."
        : $"{Issues.Count} placement problem(s): {string.Join(" ", Issues.Select(i => i.Message))}";
}

public sealed record PlacementIssue(
    /// <summary>"overlap", "duplicate-placement", "page-out-of-range" or "off-page".</summary>
    string Kind,
    int? Page,
    List<string> Signatories,
    string Message);

public sealed record PlacementPage(int Page, List<PlacementBox> Boxes);

public sealed record PlacementBox(
    string Signatory, decimal XNorm, decimal YNorm, decimal WNorm, decimal HNorm);
