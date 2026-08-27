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
        Send(settings, WhatsAppMessageBuilder.BuildChangesMessage(scrapeTarget, added, removed));
    }

    public static void SendNotification(WhatsAppSettings settings, string text) => Send(settings, text);

    private static void Send(WhatsAppSettings settings, string text)
    {
        var url = $"https://api.callmebot.com/whatsapp.php?phone={Uri.EscapeDataString(settings.PhoneNumber)}" +
                   $"&text={Uri.EscapeDataString(text)}&apikey={Uri.EscapeDataString(settings.ApiKey)}";

        var response = HttpClient.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
    }
}
