using System.Text;
using System.Text.RegularExpressions;
using WebScrapper.Scraper.Models;

namespace WebScrapper.Scraper.Email;

/// <summary>Shared plain-text message formatting, used by both the CallMeBot and
/// WhatsApp-Web-automation delivery methods so the message content stays identical.</summary>
public static class WhatsAppMessageBuilder
{
    public static string BuildChangesMessage(string scrapeTarget, List<ResearchItem> added, List<ResearchItem> removed)
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
