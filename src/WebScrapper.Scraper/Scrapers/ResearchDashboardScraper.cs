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

        Console.WriteLine($"Navigating to login page: {_settings.LoginUrl}");
        _driver.Navigate().GoToUrl(_settings.LoginUrl);

        Console.WriteLine("Filling username...");
        var usernameField = _wait.Until(d => d.FindElement(By.Id("name")));
        usernameField.SendKeys(_settings.Username);

        Console.WriteLine("Filling password...");
        var passwordField = _driver.FindElement(By.Id("password"));
        passwordField.SendKeys(_settings.Password);

        Console.WriteLine("Clicking Login button...");
        var loginButton = _driver.FindElement(By.XPath("//button[@type='submit' and contains(., 'Login')]"));
        loginButton.Click();

        // Timing-based heuristics for "login is done" (URL changed, URL settled, etc.) are
        // unreliable here: the OTP page itself doesn't navigate anywhere while it's waiting
        // for input, so it looks "settled" almost immediately — well before the OTP has
        // actually been entered and submitted. Moving on at that point (e.g. navigating to
        // the dashboard) interrupts the OTP flow and bounces back to the login page. So
        // instead of guessing, just wait for explicit confirmation.
        Console.WriteLine("If an OTP prompt appears, complete it in the browser window.");
        Console.WriteLine("Once you're fully logged in and see the dashboard, press Enter here to continue...");
        Console.ReadLine();
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
        Console.WriteLine($"Navigating to research dashboard: {_settings.TargetUrl}");
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
        Console.WriteLine($"Clicking '{assetClassTabText}' tab...");
        // Top-level asset-class tab, must be selected before the Live/Closed sub-tabs appear.
        var assetTab = _wait.Until(d => d.FindElement(By.XPath($"//button[@role='tab' and contains(., '{assetClassTabText}')]")));
        assetTab.Click();

        Console.WriteLine("Clicking 'Live' sub-tab...");
        // The "Live" tab's label includes a dynamic count, e.g. "Live (1)", so match on
        // partial text rather than the full label.
        var liveTab = _wait.Until(d => d.FindElement(By.XPath("//button[@role='tab' and contains(., 'Live')]")));
        liveTab.Click();

        Console.WriteLine("Waiting for grid rows to render...");
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

        // Grouped (not ToDictionary) because ag-Grid can include non-data rows (e.g. a
        // full-width loading/overlay row) that share an empty row-index — ToDictionary would
        // throw on the duplicate key and abort before a single row gets extracted.
        var pinnedRowsByIndex = _driver
            .FindElements(By.CssSelector("div.ag-pinned-left-cols-container div[role='row']"))
            .GroupBy(row => row.GetAttribute("row-index") ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.First());
        IReadOnlyList<IWebElement> centerRows = _driver.FindElements(By.CssSelector("div.ag-center-cols-container div[role='row']"));

        Console.WriteLine($"Found {pinnedRowsByIndex.Count} pinned row(s), {centerRows.Count} center row(s).");

        if (_settings.MaxRows is int maxRows)
        {
            centerRows = centerRows.Take(maxRows).ToList();
            Console.WriteLine($"Capped to first {centerRows.Count} row(s) (MaxRows={maxRows}).");
        }

        var items = new List<ResearchItem>();

        foreach (var centerRow in centerRows)
        {
            try
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

                var item = new ResearchItem
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
                };

                Console.WriteLine($"  [{assetClassTabText}] {item.Symbol} | Reco Date: {item.Timestamp} | LTP: {item.Ltp} | Reco Price: {item.RecoPrice}");
                items.Add(item);
            }
            catch (StaleElementReferenceException)
            {
                // ag-Grid recycled this row's DOM node mid-read (e.g. a live price update);
                // skip it rather than aborting the whole tab.
                Console.WriteLine("  Skipped a row: it was recycled by the grid while reading.");
            }
        }

        Console.WriteLine($"Extracted {items.Count} item(s) from {assetClassTabText}.");
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
