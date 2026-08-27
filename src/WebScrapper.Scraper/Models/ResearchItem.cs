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
    public DateTime ScrapedAtUtc { get; set; } = DateTime.UtcNow;
}
