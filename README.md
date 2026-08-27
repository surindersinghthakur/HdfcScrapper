# WebScrapper

A C#/.NET Selenium web scraping project.

## Structure

```
WebScrapper.sln
src/WebScrapper.Scraper/
├── Config/ScraperSettings.cs      # strongly-typed settings
├── Models/ResearchItem.cs         # scraped data shape
├── Scrapers/WebDriverFactory.cs   # Chrome driver setup (persistent profile, headless toggle)
├── Scrapers/ResearchDashboardScraper.cs
├── Email/ResearchEmailSender.cs   # emails added/removed items as HTML tables via Gmail SMTP
├── Email/WhatsAppNotifier.cs      # sends added/removed items via CallMeBot (free WhatsApp bridge)
├── Email/WhatsAppWebNotifier.cs   # alternative: drives web.whatsapp.com directly
├── Email/WhatsAppMessageBuilder.cs # shared message text, used by both WhatsApp senders
├── Data/ResearchStateStore.cs     # persists the last-seen snapshot for diffing
├── appsettings.json                # non-secret config (target URL, timeouts)
├── appsettings.local.json.example  # copy to appsettings.local.json for credentials (gitignored)
└── Program.cs
```

## Setup

```bash
cd src/WebScrapper.Scraper
dotnet restore
cp appsettings.local.json.example appsettings.local.json
# edit appsettings.local.json with your real username/password
```

Credentials can also be supplied via environment variables instead of the local file:

```bash
set Scraper__Username=your-username
set Scraper__Password=your-password
```

## Run

```bash
dotnet run --project src/WebScrapper.Scraper
```

Chrome opens (non-headless by default) so you can solve any CAPTCHA or 2FA manually — the session is cached in `chrome-profile/` so subsequent runs skip login. The process then polls indefinitely every 1 minute (Ctrl+C to stop, or press Enter during a wait to trigger the next cycle immediately) — see [Polling loop](#polling-loop) below.

### Running unattended for the trading day

`Scraper.MarketOpenTime` / `Scraper.MarketCloseTime` ("HH:mm", default `09:15`/`15:40`) make the program self-limiting:

- Not a weekday → exits immediately without launching Chrome.
- Already past close time → exits immediately.
- Before open time → waits (no Chrome launched yet) until market open, then proceeds.
- Once running, it stops polling and exits cleanly once close time is reached (a notification email is sent if `Email.Enabled`).

You still start it manually each day — this isn't OS-scheduled — but you can start it any time before market open (e.g. first thing in the morning) and walk away; it'll wait for open, run all day, and stop itself at close.

**Login on unattended days:** if `chrome-profile/`'s persisted session is still valid, `Login()` detects that the login form never appears (the site redirects straight to the dashboard) and skips straight through — no OTP, no blocking on Enter. This depends on the site's session surviving between days, which isn't guaranteed; if it doesn't, the program will block waiting for you to complete login/OTP and press Enter, same as any other run.

For quick test runs, set `Scraper.MaxRows` (e.g. `5`) to cap how many rows are read per tab, and watch each row get printed to the console as it's scraped.

## Current target

`https://investright.hdfcsec.com/dashboard/research?...` — HDFC Securities' research dashboard, behind a login (username/password, likely followed by an OTP step).

Login happens once at startup:

`Login()` fills the username (`#name`) and password (`#password`) fields and submits. If an OTP prompt appears, the (non-headless) Chrome window pauses for up to 2 minutes for you to enter it manually.

Each poll then calls `ScrapeResearch()`, which scrapes the asset-class tab selected by `Scraper.ScrapeTarget` (`"FnO"` (default) or `"Stocks"`):

1. Clicks the top-level asset-class tab (**F&O** or **Stocks** — must be selected before its Live/Closed sub-tabs appear).
2. Clicks the **Live** sub-tab (matched by partial text since its label includes a dynamic count, e.g. "Live (1)").
3. Reads the ag-Grid table. If it's empty, waits up to `TimeoutSeconds` (default 30s) and returns an empty list rather than erroring.

The grid renders each row's cells across two DOM containers — a pinned-left container for the scrip name column, and a center container for LTP/Reco Price/Potential Returns — matched up by a shared `row-index` attribute. Extraction uses ag-Grid's stable `col-id` attributes and the fixed order of `<p>` text lines within each cell, since MUI's generated `mui-xxxxx` classes are unstable across builds.

ag-Grid virtualizes rows (only rows scrolled into view exist in the DOM), so `ScrapeResearch()` programmatically scrolls the grid body (via JS `scrollTop`), extracting whatever's visible at each position and merging into a deduplicated set (keyed by Symbol+RecoPrice+Timestamp, since `row-index` gets recycled for different rows as the grid scrolls). It stops at the bottom of the grid or after a few scrolls produce no new rows.

### Per-item detail fields

Clicking a row's scrip name navigates to a detail page with additional fields — `EnrichWithDetails()` extracts **Target Price**, **Target price vaild till** (typo intentional — matches the site's actual label), and **Stoploss at** (may not exist for Stocks; left `null` rather than treated as an error) by matching on that label text, then clicks the back button (found by its SVG icon path, since it has no stable id/class) to return.

This is only called for items that are actually **new** since the last poll — running it for every row on every scrape would mean a navigate → click → extract → back round trip per row (100+ of them), which doesn't scale. It's called from `Program.cs` right before emailing, once per item in `added`.

## Polling loop

`Program.cs` logs in once, then loops forever. Each cycle runs as **two fully separate passes** when `ScrapeTarget` is `"FnO"` — Options first (scrape → diff → enrich new items → email/WhatsApp if changed), then Futures (its own scrape → diff → enrich → email/WhatsApp) — each with its own subject line and message, not merged into one notification. (`ScrapeTarget: "Stocks"` is just a single pass, no Futures concept.) Both passes' current items are merged into one combined snapshot saved at the end of the cycle. Then: wait 1 minute (or press Enter to trigger the next cycle immediately) → repeat. A failed iteration (network blip, page hiccup) is logged, emailed as a notification, and skipped rather than crashing the whole process. Ctrl+C and any fatal crash also send a notification email before exiting.

Each scraped `ResearchItem` carries a `ScrapedAtUtc` timestamp (in addition to the site's own displayed `Timestamp`), so both the local snapshot and any email show when each row was actually captured.

## Notes

- `Headless` in `appsettings.json` defaults to `false` so you can watch/debug runs and handle the OTP step.
- The Chrome user-data-dir under `chrome-profile/` persists cookies between runs — delete it to force a fresh login.

## Emailing results

Each poll only emails when something actually **changed** since the last poll: a new `Symbol + RecoPrice` combination appeared, or one that was previously there is now gone (same scrip re-appearing with a different reco price counts as new). It's off by default (`Email.Enabled: false` in `appsettings.json`).

1. Generate a Gmail **App Password**: [myaccount.google.com/apppasswords](https://myaccount.google.com/apppasswords) (requires 2-Step Verification on the account). This is a 16-character password separate from your normal Gmail login — never use your real password here.
2. In `appsettings.local.json` (gitignored — see `appsettings.local.json.example`), set:
   ```json
   {
     "Email": {
       "Enabled": true,
       "SenderEmail": "your-gmail-address@gmail.com",
       "SenderAppPassword": "your-16-char-app-password"
     }
   }
   ```
3. `RecipientEmail` defaults to `surindersinghthakur@gmail.com` in `appsettings.json` — override it in `appsettings.local.json` if needed.
4. Run as usual; each poll iteration that finds a change sends an email with "New Research Items" / "Removed Research Items" tables (only the sections that apply) and prints a confirmation to the console. No change → no email.

Credentials can also be set via environment variables instead: `Email__SenderEmail` and `Email__SenderAppPassword`.

## WhatsApp notifications (optional)

Same added/removed changes can also go out as a WhatsApp message via [CallMeBot](https://www.callmebot.com/blog/free-api-whatsapp-messages/) — a free, unofficial API that bridges to a personal WhatsApp number. Off by default (`WhatsApp.Enabled: false`).

1. Save `+34 644 59 71 67` as a contact on your phone.
2. From your phone, send it the WhatsApp message: `I allow callmebot to send me messages`
3. It replies with your personal API key within a minute or two.
4. In `appsettings.local.json`:
   ```json
   {
     "WhatsApp": {
       "Enabled": true,
       "PhoneNumber": "91XXXXXXXXXX",
       "ApiKey": "your-callmebot-api-key"
     }
   }
   ```
5. Run as usual — WhatsApp and email notifications fire independently and can be enabled together or separately.

Note: CallMeBot is a community-run bridge (not an official WhatsApp/Meta product), with a modest daily message cap and no uptime guarantee — fine for personal alerts, not for anything critical.

### Alternative: WhatsApp Web automation

Set `WhatsApp.Method` to `"WebAutomation"` to send by driving [web.whatsapp.com](https://web.whatsapp.com) directly (`WhatsAppWebNotifier.cs`) instead of going through CallMeBot — no message cap, but it needs a **second**, independent Chrome profile (`whatsapp-profile/`, alongside `chrome-profile/`) kept logged in.

- First run: a Chrome window opens to WhatsApp Web. Scan the QR code (WhatsApp app → Linked Devices → Link a Device), then press Enter in the console once you see your chat list. The session persists afterward like the HDFC one does.
- Sends via WhatsApp's "click to chat" URL (`web.whatsapp.com/send?phone=...&text=...`), which pre-fills the message — the code still clicks Send itself.
- The CSS selectors for the QR canvas / chat list / send button are based on commonly-documented WhatsApp Web patterns, not a live-inspected DOM (unlike the HDFC selectors, which were built by inspecting the real site together) — they may need adjusting after the first real test, the same way HDFC's did.

**Test without touching HDFC at all:**
```bash
dotnet run --project src/WebScrapper.Scraper -- --test-whatsapp
```
This skips the whole login/scrape flow and sends the first 2 items already sitting in `data/research-state.json` as a WhatsApp message — useful for iterating on the Web-automation selectors without waiting on a real scrape cycle.

### State and deduplication

The full snapshot of the last poll's results (not just keys) is stored in `data/research-state.json`, next to the build output alongside `chrome-profile/`. Each poll diffs the current scrape against that snapshot, keyed on `Symbol + RecoPrice`:

- Keys present now but not in the snapshot → **added**
- Keys in the snapshot but not present now → **removed**

Both directions trigger an email; the snapshot is only overwritten when there's an actual change. Delete `data/research-state.json` to reset — the next poll's items will all show up as "added".
