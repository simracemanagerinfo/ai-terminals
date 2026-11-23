using System.Globalization;
using System.Text;
using MeetingNotes;

static int Offset(string header, string key)
{
    var pos = header.IndexOf(key + ":", StringComparison.Ordinal);
    if (pos < 0)
        throw new InvalidOperationException("Missing CF_HTML field: " + key);
    return int.Parse(header.Substring(pos + key.Length + 1, 10), CultureInfo.InvariantCulture);
}

var body = "<p>Riunione con Federica: qualità, attività, città &amp; note</p>" +
    "<table><tr><td>À è ù — prova</td></tr></table>";
var cf = OutlookClipboard.BuildCfHtml("<!doctype html><html><head></head><body style=\"font-family:Arial\">" + body + "</body></html>");
var bytes = Encoding.UTF8.GetBytes(cf);
var header = cf[..cf.IndexOf("<html>", StringComparison.Ordinal)];
var startHtml = Offset(header, "StartHTML");
var endHtml = Offset(header, "EndHTML");
var start = Offset(header, "StartFragment");
var end = Offset(header, "EndFragment");

if (startHtml < 0 || start <= startHtml || end <= start || endHtml <= end || endHtml != bytes.Length)
    throw new InvalidOperationException("CF_HTML offset range is invalid.");

var segment = Encoding.UTF8.GetString(bytes[start..end]);
if (segment != body)
    throw new InvalidOperationException("CF_HTML UTF-8 fragment offsets do not match the email body.");

if (!Encoding.UTF8.GetString(bytes[startHtml..endHtml]).Contains("<!--StartFragment-->"))
    throw new InvalidOperationException("CF_HTML fragment marker is missing.");

Console.WriteLine("[PASS] Windows CF_HTML offsets, Unicode and Outlook fragment.");
