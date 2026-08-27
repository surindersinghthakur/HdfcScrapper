namespace WebScrapper.Scraper.Config;

public class ScraperSettings
{
    public string TargetUrl { get; set; } = string.Empty;
    public string? LoginUrl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool Headless { get; set; } = false;
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>"FnO", "Stocks", or "Both" — which asset-class tab(s) to scrape.</summary>
    public string ScrapeTarget { get; set; } = "FnO";

    /// <summary>Cap on rows read per tab (null = no cap). Useful for fast test runs.</summary>
    public int? MaxRows { get; set; }
}
