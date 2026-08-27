namespace WebScrapper.Scraper.Models;

public class ResearchItem
{
    public string Symbol { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Details { get; set; }
    public string? Timestamp { get; set; }
    public string? Ltp { get; set; }
    public string? Change { get; set; }
    public string? ChangePercent { get; set; }
    public string? RecoPrice { get; set; }
    public string? PotentialReturnPercent { get; set; }
    public string? Duration { get; set; }
    public string? Action { get; set; }

    // From the per-item detail page. Common to both F&O and Stocks (Stoploss may not always
    // be present); left null when a section doesn't exist rather than treated as an error.
    public string? TargetPrice { get; set; }
    public string? TargetPriceValidTill { get; set; }
    public string? StoplossAt { get; set; }

    public DateTime ScrapedAtUtc { get; set; } = DateTime.UtcNow;
}
