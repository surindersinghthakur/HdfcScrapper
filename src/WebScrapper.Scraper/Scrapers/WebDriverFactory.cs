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

        var driver = new ChromeDriver(options);
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        return driver;
    }
}
