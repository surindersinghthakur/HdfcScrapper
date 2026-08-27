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
var pollInterval = TimeSpan.FromMinutes(1);

void WaitForNextCycle(TimeSpan interval)
{
    Console.WriteLine($"Waiting {interval.TotalMinutes:0.#} min(s) for next cycle (press Enter to trigger now)...");
    var delayTask = Task.Delay(interval);
    var enterPressedTask = Task.Run(() => Console.ReadLine());
    Task.WaitAny(delayTask, enterPressedTask);
}

void NotifyIfEnabled(string body)
{
    if (!emailSettings.Enabled)
    {
        return;
    }

    try
    {
        ResearchEmailSender.SendNotification(emailSettings, body);
    }
    catch (Exception emailEx)
    {
        Console.WriteLine($"Failed to send notification email: {emailEx.Message}");
    }
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // handle shutdown ourselves so the notification email finishes first.
    Console.WriteLine("Stopping (Ctrl+C)...");
    NotifyIfEnabled($"HdfcSec scraper stopped by user (Ctrl+C) at {DateTime.Now}.");
    Environment.Exit(0);
};

var marketOpen = TimeOnly.Parse(settings.MarketOpenTime);
var marketClose = TimeOnly.Parse(settings.MarketCloseTime);

// Self-limiting: intended to be started manually each weekday, whenever convenient, and left
// unattended until market close. Doesn't launch Chrome at all if there's nothing to do today.
if (DateTime.Now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
{
    Console.WriteLine($"Today ({DateTime.Now:dddd}) is not a trading weekday. Exiting.");
    return;
}

if (TimeOnly.FromDateTime(DateTime.Now) >= marketClose)
{
    Console.WriteLine($"Market close time ({marketClose}) has already passed today. Exiting.");
    return;
}

if (TimeOnly.FromDateTime(DateTime.Now) < marketOpen)
{
    var waitSpan = DateTime.Today.Add(marketOpen.ToTimeSpan()) - DateTime.Now;
    Console.WriteLine($"Waiting until market open at {marketOpen} ({waitSpan.TotalMinutes:0} min from now)...");
    Thread.Sleep(waitSpan);
}

using var scraper = new ResearchDashboardScraper(settings);

try
{
    scraper.Login();

    var overrideCloseTime = false;
    var consecutiveFailures = 0;
    var maxBackoff = TimeSpan.FromMinutes(10);

    while (true)
    {
        if (!overrideCloseTime && TimeOnly.FromDateTime(DateTime.Now) >= marketClose)
        {
            Console.WriteLine($"Market close time ({marketClose}) reached. Press any key within 30s to keep scraping on demand, or it will stop automatically...");
            var keyPressedTask = Task.Run(() => Console.ReadKey(intercept: true));
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));

            if (Task.WaitAny(keyPressedTask, timeoutTask) == 0)
            {
                overrideCloseTime = true;
                Console.WriteLine("Continuing to scrape on demand beyond market close...");
            }
            else
            {
                Console.WriteLine("No response — stopping.");
                NotifyIfEnabled($"HdfcSec scraper stopped: market close time ({marketClose}) reached at {DateTime.Now}.");
                break;
            }
        }

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
                    // Only fetch each item's detail page (Target Price / valid-till / stoploss)
                    // for genuinely new picks -- doing this for every row on every scrape would
                    // mean a navigate-click-extract-back round trip per row, which doesn't scale.
                    foreach (var item in added)
                    {
                        try
                        {
                            scraper.EnrichWithDetails(item);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to fetch detail fields for {item.Symbol}: {ex.Message}");
                        }
                    }

                    ResearchEmailSender.SendChanges(emailSettings, settings.ScrapeTarget, added, removed);
                    Console.WriteLine($"Emailed changes to {emailSettings.RecipientEmail}.");
                }

                ResearchStateStore.Save(statePath, currentByKey.Values);
            }

            consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            // Keep the polling loop alive across transient failures (network blip, page hiccup)
            // instead of letting one bad iteration kill the whole long-running process.
            Console.WriteLine($"[{DateTime.Now:T}] Scrape iteration failed: {ex.Message}");
            NotifyIfEnabled($"HdfcSec scraper iteration failed at {DateTime.Now}:\n\n{ex}");
            consecutiveFailures++;
        }

        // Back off after repeated failures (slow/flaky internet or site) instead of hammering
        // it every minute; reset to the normal interval as soon as a scrape succeeds again.
        var waitTime = consecutiveFailures == 0
            ? pollInterval
            : TimeSpan.FromSeconds(Math.Min(pollInterval.TotalSeconds * Math.Pow(2, consecutiveFailures), maxBackoff.TotalSeconds));

        if (consecutiveFailures > 0)
        {
            Console.WriteLine($"{consecutiveFailures} consecutive failure(s) — backing off to {waitTime.TotalMinutes:0.#} min before retrying.");
        }

        WaitForNextCycle(waitTime);
    }
}
catch (Exception ex)
{
    // Anything that escapes the loop's own try/catch (e.g. during Login()) is fatal.
    Console.WriteLine($"[{DateTime.Now:T}] Scraper crashed: {ex}");
    NotifyIfEnabled($"HdfcSec scraper crashed at {DateTime.Now}:\n\n{ex}");
    throw;
}
