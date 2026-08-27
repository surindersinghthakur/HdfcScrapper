using Microsoft.Extensions.Configuration;
using WebScrapper.Scraper.Config;
using WebScrapper.Scraper.Data;
using WebScrapper.Scraper.Email;
using WebScrapper.Scraper.Scrapers;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var settings = configuration.GetSection("Scraper").Get<ScraperSettings>()
    ?? throw new InvalidOperationException("Missing 'Scraper' configuration section.");
var emailSettings = configuration.GetSection("Email").Get<EmailSettings>() ?? new EmailSettings();

var statePath = Path.Combine(AppContext.BaseDirectory, "data", "research-state.json");
var pollInterval = TimeSpan.FromMinutes(2);

using var scraper = new ResearchDashboardScraper(settings);
scraper.Login();

while (true)
{
    try
    {
        var items = scraper.ScrapeResearch();
        var currentByKey = items.ToDictionary(ResearchStateStore.DedupeKey);
        var previousByKey = ResearchStateStore.Load(statePath);

        var added = currentByKey.Keys.Except(previousByKey.Keys).Select(k => currentByKey[k]).ToList();
        var removed = previousByKey.Keys.Except(currentByKey.Keys).Select(k => previousByKey[k]).ToList();

        Console.WriteLine($"[{DateTime.Now:T}] Scraped {items.Count} item(s) — {added.Count} added, {removed.Count} removed.");

        if (added.Count > 0 || removed.Count > 0)
        {
            if (emailSettings.Enabled)
            {
                ResearchEmailSender.SendChanges(emailSettings, settings.ScrapeTarget, added, removed);
                Console.WriteLine($"Emailed changes to {emailSettings.RecipientEmail}.");
            }

            ResearchStateStore.Save(statePath, currentByKey.Values);
        }
    }
    catch (Exception ex)
    {
        // Keep the polling loop alive across transient failures (network blip, page hiccup)
        // instead of letting one bad iteration kill the whole long-running process.
        Console.WriteLine($"[{DateTime.Now:T}] Scrape iteration failed: {ex.Message}");
    }

    Console.WriteLine($"Waiting {pollInterval.TotalMinutes:0.#} min(s) for next cycle...");
    Thread.Sleep(pollInterval);
}
