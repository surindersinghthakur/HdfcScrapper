using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using WebScrapper.Scraper.Config;

namespace WebScrapper.Scraper.Scrapers;

public static class WebDriverFactory
{
    public static IWebDriver Create(ScraperSettings settings)
    {
        var options = new ChromeOptions();

        if (settings.Headless)
        {
            options.AddArgument("--headless=new");
        }

        options.AddArgument("--start-maximized");
        options.AddArgument("--disable-blink-features=AutomationControlled");

        // Persist the Chrome profile so a manual login survives across runs,
        // avoiding repeated automated logins against the site.
        var profileDir = Path.Combine(AppContext.BaseDirectory, "chrome-profile");
        options.AddArgument($"--user-data-dir={profileDir}");

        // No implicit wait: mixing it with the explicit WebDriverWait used elsewhere is a
        // known Selenium footgun. Every FindElements call that legitimately returns zero
        // results (common while probing per-row cells) would silently block for the full
        // implicit-wait duration before giving up, making failures look like a hang with
        // no error and no console output — exactly what happened when this was set.
        return new ChromeDriver(options);
    }
}
