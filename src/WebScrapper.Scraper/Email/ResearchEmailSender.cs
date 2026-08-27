using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using WebScrapper.Scraper.Config;
using WebScrapper.Scraper.Models;

namespace WebScrapper.Scraper.Email;

public static class ResearchEmailSender
{
    public static void SendChanges(EmailSettings settings, string scrapeTarget, List<ResearchItem> added, List<ResearchItem> removed)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.SenderEmail));
        message.To.Add(MailboxAddress.Parse(settings.RecipientEmail));
        message.Subject = BuildSubject(scrapeTarget, settings.Subject);
        message.Body = new TextPart("html") { Text = BuildHtml(added, removed) };

        using var client = new SmtpClient();
        client.Connect(settings.SmtpHost, settings.SmtpPort, SecureSocketOptions.StartTls);
        client.Authenticate(settings.SenderEmail, settings.SenderAppPassword);
        client.Send(message);
        client.Disconnect(true);
    }

    /// <summary>Sends a plain-text notification, e.g. when the scraper stops or crashes.</summary>
    public static void SendNotification(EmailSettings settings, string body)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.SenderEmail));
        message.To.Add(MailboxAddress.Parse(settings.RecipientEmail));
        message.Subject = "HdfcSec-Scrapper-Error";
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        client.Connect(settings.SmtpHost, settings.SmtpPort, SecureSocketOptions.StartTls);
        client.Authenticate(settings.SenderEmail, settings.SenderAppPassword);
        client.Send(message);
        client.Disconnect(true);
    }

    private static string BuildSubject(string scrapeTarget, string fallback) => scrapeTarget.ToLowerInvariant() switch
    {
        "fno" => "F&O Added - HdfcSec",
        "stocks" => "Stock Added - HdfcSec",
        _ => fallback,
    };

    private static string BuildHtml(List<ResearchItem> added, List<ResearchItem> removed)
    {
        var sb = new StringBuilder();
        sb.Append($"<p style='font-family:sans-serif;font-size:13px;color:#555'>Checked at {DateTime.UtcNow:u}</p>");

        if (added.Count > 0)
        {
            sb.Append("<h2>New Research Items</h2>");
            sb.Append(BuildTable(added));
        }

        if (removed.Count > 0)
        {
            sb.Append("<h2>Removed Research Items</h2>");
            sb.Append(BuildTable(removed));
        }

        return sb.ToString();
    }

    private static string BuildTable(List<ResearchItem> items)
    {
        var sb = new StringBuilder();
        sb.Append("<table border='1' cellpadding='6' cellspacing='0' style='border-collapse:collapse;font-family:sans-serif;font-size:14px'>");
        sb.Append("<tr style='background:#f2f2f2'>")
          .Append("<th>Name</th><th>Reco Date</th><th>Reco Price</th><th>LTP</th><th>Action</th><th>Scrap Date</th>")
          .Append("</tr>");

        foreach (var item in items)
        {
            var isSell = string.Equals(item.Action?.Trim(), "sell", StringComparison.OrdinalIgnoreCase);
            var rowStyle = isSell ? " style='background:#f8d7da'" : string.Empty;

            sb.Append($"<tr{rowStyle}>")
              .Append($"<td>{Encode(item.Symbol)}</td>")
              .Append($"<td>{Encode(item.Timestamp)}</td>")
              .Append($"<td>{Encode(item.RecoPrice)}</td>")
              .Append($"<td>{Encode(item.Ltp)}</td>")
              .Append($"<td>{Encode(item.Action)}</td>")
              .Append($"<td>{item.ScrapedAtUtc:u}</td>")
              .Append("</tr>");
        }

        sb.Append("</table>");
        return sb.ToString();
    }

    private static string Encode(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
