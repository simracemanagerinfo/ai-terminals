using System.Text;
using System.Windows;

namespace MeetingNotes;

/// <summary>
/// Produces the Windows CF_HTML clipboard format so Outlook can paste
/// headings, bullet lists and the actions table as editable email content.
/// All offsets are UTF-8 byte offsets, not .NET character indices.
/// </summary>
public static class OutlookClipboard
{
    public static void Copy(string fullHtml, string fallbackPlainText)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Html, BuildCfHtml(fullHtml));
        data.SetData(DataFormats.UnicodeText, fallbackPlainText);
        Clipboard.SetDataObject(data, true);
    }

    public static string BuildCfHtml(string fullHtml)
    {
        if (string.IsNullOrWhiteSpace(fullHtml))
            throw new ArgumentException("La minuta HTML è vuota.", nameof(fullHtml));

        var bodyBegin = fullHtml.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyBegin < 0)
            throw new FormatException("Tag body non trovato nell'HTML.");
        var bodyContentStart = fullHtml.IndexOf('>', bodyBegin);
        var bodyEnd = fullHtml.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyContentStart < 0 || bodyEnd <= bodyContentStart)
            throw new FormatException("Il corpo HTML non è valido.");

        var fragment = fullHtml.Substring(bodyContentStart + 1, bodyEnd - bodyContentStart - 1);
        const string beforeFragment = "<html><body><!--StartFragment-->";
        const string afterFragment = "<!--EndFragment--></body></html>";
        var html = beforeFragment + fragment + afterFragment;

        const string headerTemplate =
            "Version:0.9\r\n" +
            "StartHTML:{0:D10}\r\n" +
            "EndHTML:{1:D10}\r\n" +
            "StartFragment:{2:D10}\r\n" +
            "EndFragment:{3:D10}\r\n";

        var headerLength = Encoding.UTF8.GetByteCount(string.Format(
            System.Globalization.CultureInfo.InvariantCulture, headerTemplate, 0, 0, 0, 0));
        var startHtml = headerLength;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(beforeFragment);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(html);

        var header = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            headerTemplate, startHtml, endHtml, startFragment, endFragment);
        return header + html;
    }
}
