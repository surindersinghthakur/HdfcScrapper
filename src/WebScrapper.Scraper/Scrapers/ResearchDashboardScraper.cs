using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using WebScrapper.Scraper.Config;
using WebScrapper.Scraper.Models;

namespace WebScrapper.Scraper.Scrapers;

public class ResearchDashboardScraper : IDisposable
{
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;
    private readonly ScraperSettings _settings;

    public ResearchDashboardScraper(ScraperSettings settings)
    {
        _settings = settings;
        _driver = WebDriverFactory.Create(settings);
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(settings.TimeoutSeconds));
    }

    /// <summary>
    /// Logs in if credentials are configured and the profile isn't already authenticated.
    /// If the site prompts for an OTP/2FA step after this, it will show up in the
    /// (non-headless) Chrome window for the user to complete manually before ScrapeResearch runs.
    /// </summary>
    public void Login()
    {
        if (string.IsNullOrWhiteSpace(_settings.LoginUrl) ||
            string.IsNullOrWhiteSpace(_settings.Username) ||
            string.IsNullOrWhiteSpace(_settings.Password))
        {
            return;
        }

        _driver.Navigate().GoToUrl(_settings.LoginUrl);

        var usernameField = _wait.Until(d => d.FindElement(By.Id("name")));
        usernameField.SendKeys(_settings.Username);

        var passwordField = _driver.FindElement(By.Id("password"));
        passwordField.SendKeys(_settings.Password);

        var loginButton = _driver.FindElement(By.XPath("//button[@type='submit' and contains(., 'Login')]"));
        loginButton.Click();

        // HDFC Securities may prompt for an OTP after this. Give plenty of time for
        // manual entry in the (non-headless) browser window before giving up.
        Console.WriteLine("If an OTP prompt appears, enter it in the browser window now...");
        WaitForLoginToSettle();
    }

    /// <summary>
    /// Waits for the URL to move off the login page AND stay unchanged for a few seconds.
    /// A single "URL changed" check fires too early — clicking Login often lands on an
    /// intermediate OTP page first, and moving on right then (e.g. navigating to the
    /// dashboard while the user is still typing the OTP) interrupts that flow and bounces
    /// back to the login page. Waiting for the URL to settle avoids that.
    /// </summary>
    private void WaitForLoginToSettle()
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        var settleTime = TimeSpan.FromSeconds(3);
        string? lastUrl = null;
        var lastChangeAt = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            var currentUrl = _driver.Url;

            if (currentUrl != lastUrl)
            {
                lastUrl = currentUrl;
                lastChangeAt = DateTime.UtcNow;
            }
            else if (currentUrl != _settings.LoginUrl && DateTime.UtcNow - lastChangeAt >= settleTime)
            {
                return;
            }

            Thread.Sleep(500);
        }

        throw new TimeoutException("Timed out waiting for login to complete.");
    }

    /// <summary>
    /// Navigates to the research dashboard and extracts research items from the Live sub-tab
    /// of the F&amp;O and/or Stocks asset-class tabs, per <see cref="ScraperSettings.ScrapeTarget"/>.
    /// The grid splits each row's cells across two DOM containers (pinned-left for the scrip
    /// name column, center for the rest), so rows are matched up by their shared "row-index"
    /// attribute. MUI's generated class names (mui-xxxxx) are unstable across builds, so
    /// extraction relies on ag-Grid's stable "col-id" attributes and the fixed ordering of the
    /// &lt;p&gt; text lines within each cell instead. NOTE: ag-Grid virtualizes rows, so only
    /// rows currently scrolled into view are present in the DOM — scrolling the grid body
    /// would be needed to collect more.
    /// </summary>
    public List<ResearchItem> ScrapeResearch()
    {
        _driver.Navigate().GoToUrl(_settings.TargetUrl);

        var items = new List<ResearchItem>();

        if (_settings.ScrapeTarget.Equals("FnO", StringComparison.OrdinalIgnoreCase) ||
            _settings.ScrapeTarget.Equals("Both", StringComparison.OrdinalIgnoreCase))
        {
            items.AddRange(ScrapeAssetClassTab("F&O"));
        }

        if (_settings.ScrapeTarget.Equals("Stocks", StringComparison.OrdinalIgnoreCase) ||
            _settings.ScrapeTarget.Equals("Both", StringComparison.OrdinalIgnoreCase))
        {
            items.AddRange(ScrapeAssetClassTab("Stocks"));
        }

        return items;
    }

    /// <summary>
    /// Clicks the given top-level asset-class tab (e.g. "F&amp;O" or "Stocks"), then its "Live"
    /// sub-tab, and extracts the currently rendered ag-Grid rows. Both tabs share the same
    /// structure. NOTE: when scraping "Both", switching tabs briefly leaves the previous tab's
    /// rows in the DOM before ag-Grid re-renders — the cell-level wait below narrows this
    /// window but can't fully eliminate it.
    /// </summary>
    private List<ResearchItem> ScrapeAssetClassTab(string assetClassTabText)
    {
        // Top-level asset-class tab, must be selected before the Live/Closed sub-tabs appear.
        var assetTab = _wait.Until(d => d.FindElement(By.XPath($"//button[@role='tab' and contains(., '{assetClassTabText}')]")));
        assetTab.Click();

        // The "Live" tab's label includes a dynamic count, e.g. "Live (1)", so match on
        // partial text rather than the full label.
        var liveTab = _wait.Until(d => d.FindElement(By.XPath("//button[@role='tab' and contains(., 'Live')]")));
        liveTab.Click();

        // Wait for the actual cells, not just the row containers: ag-Grid inserts row divs
        // before populating their cells, so checking row count alone is a race condition.
        // If the Live table is empty, there's nothing to wait for — time out gracefully
        // (within the configured TimeoutSeconds) instead of throwing.
        try
        {
            _wait.Until(d => d.FindElements(By.CssSelector("div.ag-center-cols-container [col-id='ltp']")).Count > 0);
        }
        catch (WebDriverTimeoutException)
        {
            Console.WriteLine($"No rows appeared in the {assetClassTabText} Live table within {_settings.TimeoutSeconds}s — treating as empty.");
            return new List<ResearchItem>();
        }

        var pinnedRowsByIndex = _driver
            .FindElements(By.CssSelector("div.ag-pinned-left-cols-container div[role='row']"))
            .ToDictionary(row => row.GetAttribute("row-index") ?? string.Empty);
        var centerRows = _driver.FindElements(By.CssSelector("div.ag-center-cols-container div[role='row']"));

        var items = new List<ResearchItem>();

        foreach (var centerRow in centerRows)
        {
            if (!pinnedRowsByIndex.TryGetValue(centerRow.GetAttribute("row-index") ?? string.Empty, out var pinnedRow))
            {
                continue;
            }

            var scripNameCells = pinnedRow.FindElements(By.CssSelector("[col-id='scripName']"));
            var ltpCells = centerRow.FindElements(By.CssSelector("[col-id='ltp']"));
            var returnsCells = centerRow.FindElements(By.CssSelector("[col-id='potentialReturns']"));

            if (scripNameCells.Count == 0 || ltpCells.Count == 0 || returnsCells.Count == 0)
            {
                // Row div exists but ag-Grid hasn't finished populating its cells yet; skip it
                // rather than crash — a re-run (or a longer wait upstream) will pick it up.
                continue;
            }

            var nameLines = scripNameCells[0].FindElements(By.CssSelector("p.MuiTypography-root"));
            var ltpLines = ltpCells[0].FindElements(By.CssSelector("p.MuiTypography-root"));

            var recoPrice = centerRow.FindElements(By.CssSelector("[col-id='recoPrice'] p.MuiTypography-root"))
                .FirstOrDefault()?.Text;

            var returnsCell = returnsCells[0];
            var returnsLines = returnsCell.FindElements(By.CssSelector("p.MuiTypography-root"));

            items.Add(new ResearchItem
            {
                Category = nameLines.ElementAtOrDefault(0)?.Text,
                Symbol = nameLines.ElementAtOrDefault(1)?.Text ?? string.Empty,
                Details = nameLines.ElementAtOrDefault(2)?.Text,
                Timestamp = nameLines.ElementAtOrDefault(3)?.Text,
                Ltp = ltpLines.ElementAtOrDefault(0)?.Text,
                Change = ltpLines.ElementAtOrDefault(1)?.Text,
                ChangePercent = ltpLines.ElementAtOrDefault(2)?.Text,
                RecoPrice = recoPrice,
                PotentialReturnPercent = returnsLines.ElementAtOrDefault(0)?.Text,
                Duration = returnsLines.ElementAtOrDefault(1)?.Text,
                Action = TryGetText(returnsCell, "button"),
            });
        }

        return items;
    }

    private static string? TryGetText(IWebElement scope, string cssSelector)
    {
        try
        {
            return scope.FindElement(By.CssSelector(cssSelector)).Text;
        }
        catch (NoSuchElementException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _driver.Quit();
        _driver.Dispose();
    }
}
