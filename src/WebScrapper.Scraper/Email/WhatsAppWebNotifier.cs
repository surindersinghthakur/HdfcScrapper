using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using WebScrapper.Scraper.Config;
using WebScrapper.Scraper.Models;

namespace WebScrapper.Scraper.Email;

/// <summary>
/// Sends WhatsApp messages by automating web.whatsapp.com directly, using a second, independent
/// Chrome profile from the HDFC scraper's own session. Requires a one-time QR-code scan on
/// first use; the session then persists in "whatsapp-profile/" like the main "chrome-profile/"
/// does for HDFC.
///
/// NOTE: the CSS selectors below (QR canvas, chat list, send button) are based on commonly
/// documented WhatsApp Web patterns, not a live-inspected DOM — WhatsApp Web's markup changes
/// periodically and these may need adjusting after the first real test run, the same way the
/// HDFC selectors did.
/// </summary>
public class WhatsAppWebNotifier : IDisposable
{
    private const string QrCodeSelector = "canvas[aria-label='Scan this QR code to link a device!']";
    private const string ChatListSelector = "div#side";
    private const string SendButtonSelector = "button[aria-label='Send'], span[data-icon='send']";

    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;
    private readonly WhatsAppSettings _settings;
    private bool _loggedIn;

    public WhatsAppWebNotifier(WhatsAppSettings settings)
    {
        _settings = settings;

        var options = new ChromeOptions();
        options.AddArgument("--start-maximized");
        var profileDir = Path.Combine(AppContext.BaseDirectory, "whatsapp-profile");
        options.AddArgument($"--user-data-dir={profileDir}");

        _driver = new ChromeDriver(options);
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(30));
    }

    /// <summary>Opens WhatsApp Web and, if the persisted session has expired, waits for the
    /// user to scan the QR code before continuing.</summary>
    public void EnsureLoggedIn()
    {
        if (_loggedIn)
        {
            return;
        }

        Console.WriteLine("Opening WhatsApp Web...");
        _driver.Navigate().GoToUrl("https://web.whatsapp.com");

        // Give the page a moment to decide whether it's showing the QR code or the chat list
        // (persisted session) before deciding whether to prompt for a scan.
        try
        {
            var shortWait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
            shortWait.Until(d => d.FindElements(By.CssSelector($"{QrCodeSelector}, {ChatListSelector}")).Count > 0);
        }
        catch (WebDriverTimeoutException)
        {
            // Fall through — the manual-confirmation prompt below is the fallback either way.
        }

        var chatListVisible = _driver.FindElements(By.CssSelector(ChatListSelector)).Count > 0;
        if (!chatListVisible)
        {
            Console.WriteLine("Scan the QR code shown in the browser window: WhatsApp app > Linked Devices > Link a Device.");
            Console.WriteLine("Once you see your chat list, press Enter here to continue...");
            Console.ReadLine();
        }

        _loggedIn = true;
    }

    public void SendChanges(string scrapeTarget, List<ResearchItem> added, List<ResearchItem> removed)
    {
        Send(WhatsAppMessageBuilder.BuildChangesMessage(scrapeTarget, added, removed));
    }

    public void SendNotification(string text) => Send(text);

    private void Send(string text)
    {
        EnsureLoggedIn();

        // WhatsApp Web's "click to chat" URL opens the given chat with the message pre-filled
        // in the text box — it does not send automatically, so the Send button still needs a
        // click after this.
        var url = $"https://web.whatsapp.com/send?phone={Uri.EscapeDataString(_settings.PhoneNumber)}&text={Uri.EscapeDataString(text)}";
        _driver.Navigate().GoToUrl(url);
        Console.WriteLine($"  Navigated to send URL. Current URL: {_driver.Url}");

        // WhatsApp sometimes shows an intermediate "Continue to Chat" landing page (e.g. for
        // numbers not already saved as a contact) before the actual chat opens. Checking for it
        // immediately after navigation (no wait) misses it entirely, since the SPA needs a
        // moment to render either state — wait for whichever one shows up first.
        const string ContinueToChatXPath = "//a[contains(., 'Continue to Chat')] | //button[contains(., 'Continue to Chat')]";
        try
        {
            _wait.Until(d => d.FindElements(By.XPath($"{ContinueToChatXPath} | //button[@aria-label='Send'] | //span[@data-icon='send']")).Count > 0);
        }
        catch (WebDriverTimeoutException)
        {
            // Fall through — the explicit send-button wait below will surface a clearer error.
        }

        var continueButton = _driver.FindElements(By.XPath(ContinueToChatXPath)).FirstOrDefault();
        if (continueButton != null)
        {
            Console.WriteLine("  Found 'Continue to Chat' landing page, clicking through...");
            continueButton.Click();
        }

        var sendButton = _wait.Until(d => d.FindElement(By.CssSelector(SendButtonSelector)));

        // Selenium's native Click() can throw "element not interactable" here even when the
        // button is the correct one -- WhatsApp Web's send button sits inside layered/animated
        // wrapper divs that can trip up Selenium's visibility/overlap checks. Clicking via JS
        // dispatches the click directly on the element without those checks.
        try
        {
            sendButton.Click();
        }
        catch (ElementNotInteractableException)
        {
            Console.WriteLine("  Native click failed (element not interactable) — retrying via JavaScript click...");
            ((IJavaScriptExecutor)_driver).ExecuteScript("arguments[0].click();", sendButton);
        }

        Console.WriteLine($"  Clicked send. URL now: {_driver.Url}");
    }

    public void Dispose()
    {
        _driver.Quit();
        _driver.Dispose();
    }
}
