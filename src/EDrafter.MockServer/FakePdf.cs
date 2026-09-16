using System.Text;

namespace EDrafter.MockServer;

/// <summary>
/// A minimal but genuinely valid single-page PDF, so downloads in the POC open in a
/// real viewer and can be merged by the PDF pipeline. Not a real e-stamp, obviously —
/// it exists so the plumbing is exercised end to end without spending money.
/// </summary>
public static class FakePdf
{
    public static byte[] Build(string text)
    {
        var lines = text.Split('\n');
        var content = new StringBuilder();
        content.Append("BT\n/F1 14 Tf\n72 720 Td\n16 TL\n");
        foreach (var line in lines)
            content.Append($"({Escape(line)}) Tj\nT*\n");
        content.Append("ET");

        var stream = content.ToString();

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var offsets = new List<int>();
        foreach (var (body, i) in objects.Select((b, i) => (b, i + 1)))
        {
            offsets.Add(sb.Length);
            sb.Append($"{i} 0 obj\n{body}\nendobj\n");
        }

        var xrefPos = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append($"{off:D10} 00000 n \n");

        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xrefPos}\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
