namespace WebScrapper.Scraper.Config;

public class WhatsAppSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>Phone number with country code, no leading '+' (e.g. "9198XXXXXXXX").</summary>
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>Obtained by messaging CallMeBot's WhatsApp number once — see README.</summary>
    public string ApiKey { get; set; } = string.Empty;
}
