using System.Text.RegularExpressions;
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
    /// of the asset-class tab selected by <see cref="ScraperSettings.ScrapeTarget"/> ("FnO" or
    /// "Stocks"). The grid splits each row's cells across two DOM containers (pinned-left for
    /// the scrip name column, center for the rest), so rows are matched up by their shared
    /// "row-index" attribute. MUI's generated class names (mui-xxxxx) are unstable across
    /// builds, so extraction relies on ag-Grid's stable "col-id" attributes and the fixed
    /// ordering of the &lt;p&gt; text lines within each cell instead. ag-Grid virtualizes rows
    /// (only rows scrolled into view exist in the DOM), so the grid body is scrolled
    /// programmatically to collect every row, not just the initially visible ones.
    /// </summary>
    public List<ResearchItem> ScrapeResearch()
    {
        Console.WriteLine($"Navigating to research dashboard: {_settings.TargetUrl}");
        _driver.Navigate().GoToUrl(_settings.TargetUrl);

        var assetClassTabText = _settings.ScrapeTarget.Equals("Stocks", StringComparison.OrdinalIgnoreCase)
            ? "Stocks"
            : "F&O";

        return ScrapeAssetClassTab(assetClassTabText);
    }

    /// <summary>
    /// Clicks the given top-level asset-class tab (e.g. "F&amp;O" or "Stocks"), then its "Live"
    /// sub-tab, and extracts the currently rendered ag-Grid rows. Both tabs share the same
    /// structure.
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

        Console.WriteLine("Locating the research grid (the page has other ag-Grid instances, e.g. a watchlist widget)...");
        // The dashboard page hosts more than one ag-Grid instance (a watchlist/search widget
        // was observed mixed in). Unscoped row/cell queries pull rows from ALL of them combined
        // — row-indexes from different grids collide (both can have a "row-index=0"), so a
        // pinned-vs-center row built from unscoped queries can end up matching across grids
        // entirely, which is why ltp/returns cells looked permanently missing regardless of
        // how long we waited or retried. Scope every subsequent query to the one grid whose
        // headers include "scripName", which is unique to the research table.
        var gridRoot = _wait.Until(d => d.FindElement(
            By.XPath("//div[contains(@class,'ag-root-wrapper')][.//*[@col-id='scripName']]")));

        Console.WriteLine("Waiting for grid rows to render...");
        // Wait for row-index 0's ltp cell specifically to have actual text — not just "some
        // ltp cell exists anywhere" — since ag-Grid can populate cells for some rows before
        // others right after a tab switch.
        // If the Live table is empty, there's nothing to wait for — time out gracefully
        // (within the configured TimeoutSeconds) instead of throwing.
        try
        {
            _wait.Until(_ =>
            {
                var firstLtpCell = gridRoot.FindElements(By.CssSelector("div.ag-center-cols-container div[role='row'][row-index='0'] [col-id='ltp']")).FirstOrDefault();
                return firstLtpCell != null && !string.IsNullOrWhiteSpace(firstLtpCell.Text);
            });
        }
        catch (WebDriverTimeoutException)
        {
            Console.WriteLine($"No rows appeared in the {assetClassTabText} Live table within {_settings.TimeoutSeconds}s — treating as empty.");
            return new List<ResearchItem>();
        }

        // ag-Grid virtualizes rows: only rows currently scrolled into view exist in the DOM,
        // and row-index gets recycled for different data as the grid scrolls. So collect into
        // a dictionary keyed by the row's own data (Symbol+RecoPrice+Timestamp), not row-index,
        // and repeatedly extract-then-scroll until reaching the bottom or no new rows appear.
        var scrollContainer = gridRoot.FindElement(By.CssSelector(".ag-body-viewport"));
        var js = (IJavaScriptExecutor)_driver;
        var collected = new Dictionary<string, ResearchItem>();
        var debugDumped = false;
        var previousCount = -1;
        var stableIterations = 0;
        const int maxStableIterations = 3;

        while (true)
        {
            ExtractVisibleRows(gridRoot, assetClassTabText, collected, ref debugDumped);

            if (_settings.MaxRows is int maxRows && collected.Count >= maxRows)
            {
                Console.WriteLine($"Reached MaxRows={maxRows}, stopping scroll.");
                break;
            }

            if (collected.Count == previousCount)
            {
                stableIterations++;
                if (stableIterations >= maxStableIterations)
                {
                    Console.WriteLine("No new rows after several scrolls — assuming end of list.");
                    break;
                }
            }
            else
            {
                stableIterations = 0;
            }
            previousCount = collected.Count;

            var atBottom = (bool)(js.ExecuteScript(
                "var el = arguments[0]; return (el.scrollTop + el.clientHeight) >= (el.scrollHeight - 2);",
                scrollContainer) ?? false);

            if (atBottom)
            {
                Console.WriteLine("Reached bottom of grid.");
                break;
            }

            js.ExecuteScript("arguments[0].scrollTop += arguments[0].clientHeight;", scrollContainer);
            Thread.Sleep(600); // let ag-Grid render newly virtualized rows after the scroll
        }

        var items = _settings.MaxRows is int cap ? collected.Values.Take(cap).ToList() : collected.Values.ToList();
        Console.WriteLine($"Extracted {items.Count} unique item(s) from {assetClassTabText}.");
        return items;
    }

    /// <summary>
    /// Reads whatever rows are currently rendered in the grid and merges newly seen ones
    /// (keyed by Symbol+RecoPrice+Timestamp) into <paramref name="collected"/>. Safe to call
    /// repeatedly across scroll positions — rows already collected are silently skipped.
    /// </summary>
    private void ExtractVisibleRows(IWebElement gridRoot, string assetClassTabText, Dictionary<string, ResearchItem> collected, ref bool debugDumped)
    {
        // Grouped (not ToDictionary) because ag-Grid can include non-data rows (e.g. a
        // full-width loading/overlay row) that share an empty row-index — ToDictionary would
        // throw on the duplicate key and abort before a single row gets extracted.
        var pinnedRowsByIndex = gridRoot
            .FindElements(By.CssSelector("div.ag-pinned-left-cols-container div[role='row']"))
            .GroupBy(row => row.GetAttribute("row-index") ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.First());
        var centerRows = gridRoot.FindElements(By.CssSelector("div.ag-center-cols-container div[role='row']"));

        foreach (var centerRow in centerRows)
        {
            var rowIndex = centerRow.GetAttribute("row-index") ?? string.Empty;

            try
            {
                if (!pinnedRowsByIndex.TryGetValue(rowIndex, out var pinnedRow))
                {
                    // Expected transiently while the pinned column catches up mid-scroll.
                    continue;
                }

                // Live LTP data means ag-Grid keeps recycling this row's cell DOM nodes (they
                // were confirmed populated a moment ago by the wait above, yet can already be
                // empty again by the time we read them here) — retry briefly before giving up.
                IReadOnlyList<IWebElement> scripNameCells = Array.Empty<IWebElement>();
                IReadOnlyList<IWebElement> ltpCells = Array.Empty<IWebElement>();
                IReadOnlyList<IWebElement> returnsCells = Array.Empty<IWebElement>();

                for (var attempt = 0; attempt < 5; attempt++)
                {
                    scripNameCells = pinnedRow.FindElements(By.CssSelector("[col-id='scripName']"));
                    ltpCells = centerRow.FindElements(By.CssSelector("[col-id='ltp']"));
                    returnsCells = centerRow.FindElements(By.CssSelector("[col-id='potentialReturns']"));

                    if (scripNameCells.Count > 0 && ltpCells.Count > 0 && returnsCells.Count > 0)
                    {
                        break;
                    }

                    Thread.Sleep(300);
                }

                if (scripNameCells.Count == 0 || ltpCells.Count == 0 || returnsCells.Count == 0)
                {
                    // Row div exists but ag-Grid hasn't finished populating its cells yet; skip it
                    // rather than crash — the next extraction pass will pick it up.
                    if (!debugDumped)
                    {
                        debugDumped = true;
                        Console.WriteLine($"  DEBUG center row outerHTML: {centerRow.GetAttribute("outerHTML")}");
                        Console.WriteLine($"  DEBUG pinned row outerHTML: {pinnedRow.GetAttribute("outerHTML")}");
                    }

                    continue;
                }

                var nameLines = scripNameCells[0].FindElements(By.CssSelector("p.MuiTypography-root"));
                var (category, symbol, details, timestamp) = ParseScripNameLines(nameLines);
                var ltpLines = ltpCells[0].FindElements(By.CssSelector("p.MuiTypography-root"));

                var recoPrice = centerRow.FindElements(By.CssSelector("[col-id='recoPrice'] p.MuiTypography-root"))
                    .FirstOrDefault()?.Text;

                var returnsCell = returnsCells[0];
                var returnsLines = returnsCell.FindElements(By.CssSelector("p.MuiTypography-root"));

                var item = new ResearchItem
                {
                    Category = category,
                    Symbol = symbol,
                    Details = details,
                    Timestamp = timestamp,
                    Ltp = ltpLines.ElementAtOrDefault(0)?.Text,
                    Change = ltpLines.ElementAtOrDefault(1)?.Text,
                    ChangePercent = ltpLines.ElementAtOrDefault(2)?.Text,
                    RecoPrice = recoPrice,
                    PotentialReturnPercent = returnsLines.ElementAtOrDefault(0)?.Text,
                    Duration = returnsLines.ElementAtOrDefault(1)?.Text,
                    Action = TryGetText(returnsCell, "button"),
                };

                var key = $"{item.Symbol}|{item.RecoPrice}|{item.Timestamp}";
                if (collected.TryAdd(key, item))
                {
                    Console.WriteLine($"  [{assetClassTabText}] {item.Symbol} | Reco Date: {item.Timestamp} | LTP: {item.Ltp} | Reco Price: {item.RecoPrice}");
                }
            }
            catch (StaleElementReferenceException)
            {
                // ag-Grid recycled this row's DOM node mid-read (e.g. while scrolling); skip it.
            }
        }
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

    private static readonly Regex TimestampPattern = new(@"\d{1,2}:\d{2}\s*(AM|PM)", RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses the scrip-name cell's &lt;p&gt; lines by content pattern rather than fixed
    /// position — some rows (observed on F&amp;O index/futures contracts) have no category chip
    /// at all, which shifts every subsequent line's position by one and silently corrupts a
    /// fixed-index read (Details ends up read as Symbol, Timestamp is lost entirely). The
    /// Details line always contains "•" separators and the Timestamp line always matches a
    /// time-of-day pattern, so those two are identified by content; whatever's left is Category
    /// (if two lines remain) and/or Symbol (the last of what's left).
    /// </summary>
    private static (string? Category, string Symbol, string? Details, string? Timestamp) ParseScripNameLines(
        IReadOnlyList<IWebElement> nameLines)
    {
        var lineTexts = nameLines.Select(el => el.Text).ToList();

        int? detailsIndex = null;
        int? timestampIndex = null;
        for (var i = 0; i < lineTexts.Count; i++)
        {
            if (detailsIndex is null && lineTexts[i].Contains('•'))
            {
                detailsIndex = i;
            }
            else if (timestampIndex is null && TimestampPattern.IsMatch(lineTexts[i]))
            {
                timestampIndex = i;
            }
        }

        var remaining = Enumerable.Range(0, lineTexts.Count)
            .Where(i => i != detailsIndex && i != timestampIndex)
            .Select(i => lineTexts[i])
            .ToList();

        var category = remaining.Count >= 2 ? remaining[0] : null;
        var symbol = remaining.Count >= 1 ? remaining[^1] : string.Empty;
        var details = detailsIndex.HasValue ? lineTexts[detailsIndex.Value] : null;
        var timestamp = timestampIndex.HasValue ? lineTexts[timestampIndex.Value] : null;

        return (category, symbol, details, timestamp);
    }

    /// <summary>
    /// Opens the given item's detail view (by clicking its scrip name in the grid) and fills
    /// in TargetPrice/TargetPriceValidTill/StoplossAt. Only called for items that are actually
    /// new since the last poll — doing this for every row on every scrape would mean one
    /// navigate-click-extract round trip per row (there can be 100+), which is far too slow to
    /// run every cycle.
    /// </summary>
    public void EnrichWithDetails(ResearchItem item)
    {
        var assetClassTabText = _settings.ScrapeTarget.Equals("Stocks", StringComparison.OrdinalIgnoreCase)
            ? "Stocks"
            : "F&O";

        Console.WriteLine($"Fetching detail fields for {item.Symbol}...");

        // The detail view is an in-place client-side state change, not a real page navigation
        // (the URL never changes when you click into it — confirmed by observation). So there's
        // no real "back" to click or browser history to walk; the fresh GoToUrl here on every
        // call is what actually resets us to the grid, regardless of whatever detail-view state
        // was left over from the previous call. (Navigate().Back() was tried here previously and
        // is wrong for the same reason: it walks real browser history, which has nothing to do
        // with this in-app state, and drifts further off-course with every call.)
        _driver.Navigate().GoToUrl(_settings.TargetUrl);
        _wait.Until(d => d.FindElement(By.XPath($"//button[@role='tab' and contains(., '{assetClassTabText}')]"))).Click();
        _wait.Until(d => d.FindElement(By.XPath("//button[@role='tab' and contains(., 'Live')]"))).Click();

        var gridRoot = _wait.Until(d => d.FindElement(
            By.XPath("//div[contains(@class,'ag-root-wrapper')][.//*[@col-id='scripName']]")));

        var scripNameCell = FindScripNameCellBySymbol(gridRoot, item.Symbol);
        if (scripNameCell == null)
        {
            Console.WriteLine($"  Could not locate {item.Symbol} in the grid to fetch details — skipping.");
            return;
        }

        scripNameCell.Click();

        try
        {
            // Wait for the field we actually need (Target Price is expected on both F&O and
            // Stocks) as the signal that the detail view has opened.
            _wait.Until(d => d.FindElements(By.XPath(".//p[normalize-space(text())='Target Price']")).Count > 0);

            item.TargetPrice = TryGetSiblingValue(_driver, "Target Price");
            item.TargetPriceValidTill = TryGetSiblingValue(_driver, "Target price vaild till");
            item.StoplossAt = TryGetSiblingValue(_driver, "Stoploss at");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed reading detail fields for {item.Symbol}: {ex.Message}");
        }
    }

    /// <summary>Scrolls the grid looking for the pinned scrip-name cell matching the given symbol.</summary>
    private IWebElement? FindScripNameCellBySymbol(IWebElement gridRoot, string symbol)
    {
        var scrollContainer = gridRoot.FindElement(By.CssSelector(".ag-body-viewport"));
        var js = (IJavaScriptExecutor)_driver;

        for (var i = 0; i < 100; i++)
        {
            var match = gridRoot
                .FindElements(By.CssSelector("div.ag-pinned-left-cols-container [col-id='scripName']"))
                .FirstOrDefault(cell => cell.FindElements(By.CssSelector("p.MuiTypography-root")).ElementAtOrDefault(1)?.Text == symbol);

            if (match != null)
            {
                return match;
            }

            var atBottom = (bool)(js.ExecuteScript(
                "var el = arguments[0]; return (el.scrollTop + el.clientHeight) >= (el.scrollHeight - 2);",
                scrollContainer) ?? false);

            if (atBottom)
            {
                return null;
            }

            js.ExecuteScript("arguments[0].scrollTop += arguments[0].clientHeight;", scrollContainer);
            Thread.Sleep(600);
        }

        return null;
    }

    private static string? TryGetSiblingValue(ISearchContext scope, string labelText)
    {
        try
        {
            return scope.FindElement(By.XPath($".//p[normalize-space(text())='{labelText}']/following-sibling::p[1]")).Text;
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
