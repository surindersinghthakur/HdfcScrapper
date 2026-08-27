namespace WebScrapper.Scraper.Config;

public class EmailSettings
{
    public bool Enabled { get; set; } = false;
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderAppPassword { get; set; } = string.Empty;
    public string RecipientEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = "Research Dashboard Scrape Results";
}
