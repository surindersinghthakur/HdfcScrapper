namespace WebScrapper.Scraper.Config;

public class ScraperSettings
{
    public string TargetUrl { get; set; } = string.Empty;
    public string? LoginUrl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool Headless { get; set; } = false;
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>"FnO" or "Stocks" — which asset-class tab to scrape.</summary>
    public string ScrapeTarget { get; set; } = "FnO";

    /// <summary>Cap on rows read per tab (null = no cap). Useful for fast test runs.</summary>
    public int? MaxRows { get; set; }

    /// <summary>"HH:mm" — the program waits until this time if started earlier (weekdays only).</summary>
    public string MarketOpenTime { get; set; } = "09:15";

    /// <summary>"HH:mm" — the program stops polling and exits once this time is reached.</summary>
    public string MarketCloseTime { get; set; } = "15:40";
}
