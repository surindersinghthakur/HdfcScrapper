using System.Text;
using System.Text.RegularExpressions;
using WebScrapper.Scraper.Config;
using WebScrapper.Scraper.Models;

namespace WebScrapper.Scraper.Email;

/// <summary>
/// Sends WhatsApp messages via CallMeBot (https://www.callmebot.com/blog/free-api-whatsapp-messages/),
/// a free unofficial bridge to a personal WhatsApp number. One-time setup: message the CallMeBot
/// number from your own phone to receive an API key (see README).
/// </summary>
public static class WhatsAppNotifier
{
    private static readonly HttpClient HttpClient = new();

    public static void SendChanges(WhatsAppSettings settings, string scrapeTarget, List<ResearchItem> added, List<ResearchItem> removed)
    {
        Send(settings, BuildChangesMessage(scrapeTarget, added, removed));
    }

    public static void SendNotification(WhatsAppSettings settings, string text) => Send(settings, text);

    private static void Send(WhatsAppSettings settings, string text)
    {
        var url = $"https://api.callmebot.com/whatsapp.php?phone={Uri.EscapeDataString(settings.PhoneNumber)}" +
                   $"&text={Uri.EscapeDataString(text)}&apikey={Uri.EscapeDataString(settings.ApiKey)}";

        var response = HttpClient.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
    }

    private static string BuildChangesMessage(string scrapeTarget, List<ResearchItem> added, List<ResearchItem> removed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"*HdfcSec {scrapeTarget}*: {added.Count} added, {removed.Count} removed");

        if (added.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("*New:*");
            foreach (var item in added)
            {
                AppendItem(sb, item);
            }
        }

        if (removed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("*Removed:*");
            foreach (var item in removed)
            {
                AppendItem(sb, item);
            }
        }

        return sb.ToString();
    }

    private static void AppendItem(StringBuilder sb, ResearchItem item)
    {
        sb.AppendLine();
        sb.AppendLine($"Name: {BuildNameLine(item)}");
        sb.AppendLine($"*RecoPrice*: {item.RecoPrice}");
        sb.AppendLine($"TargetPrice: {item.TargetPrice}");
        sb.AppendLine($"LTP: {item.Ltp}");
    }

    private static readonly Regex DatePartPattern = new(@"^\d{1,2}\s+[A-Za-z]{3,}\s+\d{4}$");

    /// <summary>
    /// Combines Symbol with Details (e.g. "Sensex • 27 Aug 2026 • 77200 • Put"), dropping the
    /// date part since it's redundant with the site's own reco date shown elsewhere.
    /// </summary>
    private static string BuildNameLine(ResearchItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Details))
        {
            return item.Symbol;
        }

        var partsWithoutDate = item.Details
            .Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !DatePartPattern.IsMatch(part));

        var detailsWithoutDate = string.Join(" • ", partsWithoutDate);

        return string.IsNullOrEmpty(detailsWithoutDate) ? item.Symbol : $"{item.Symbol} ({detailsWithoutDate})";
    }
}
