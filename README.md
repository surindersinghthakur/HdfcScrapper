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

Chrome opens (non-headless by default) so you can solve any CAPTCHA or 2FA manually — the session is cached in `chrome-profile/` so subsequent runs skip login. The process then polls indefinitely every 2 minutes (Ctrl+C to stop) — see [Polling loop](#polling-loop) below.

## Current target

`https://investright.hdfcsec.com/dashboard/research?...` — HDFC Securities' research dashboard, behind a login (username/password, likely followed by an OTP step).

Login happens once at startup:

`Login()` fills the username (`#name`) and password (`#password`) fields and submits. If an OTP prompt appears, the (non-headless) Chrome window pauses for up to 2 minutes for you to enter it manually.

Each poll then calls `ScrapeResearch()`, which — per `Scraper.ScrapeTarget` (`"FnO"` (default), `"Stocks"`, or `"Both"`) — scrapes one or both of the asset-class tabs. For each one, it:

1. Clicks the top-level asset-class tab (**F&O** or **Stocks** — must be selected before its Live/Closed sub-tabs appear).
2. Clicks the **Live** sub-tab (matched by partial text since its label includes a dynamic count, e.g. "Live (1)").
3. Reads the ag-Grid table. If it's empty, waits up to `TimeoutSeconds` (default 30s) and returns an empty list rather than erroring.

The grid renders each row's cells across two DOM containers — a pinned-left container for the scrip name column, and a center container for LTP/Reco Price/Potential Returns — matched up by a shared `row-index` attribute. Extraction uses ag-Grid's stable `col-id` attributes and the fixed order of `<p>` text lines within each cell, since MUI's generated `mui-xxxxx` classes are unstable across builds.

**Known limitations:**
- ag-Grid virtualizes rows, so only rows currently scrolled into view exist in the DOM. `ScrapeResearch()` only reads what's rendered — scrolling the grid body would be needed to collect additional rows if the dashboard ever shows more than fit on screen.
- With `ScrapeTarget: "Both"`, switching from one asset-class tab to the other briefly leaves the previous tab's rows in the DOM before ag-Grid re-renders. The cell-level wait narrows this window but can't fully eliminate it — a rare stale read is possible right at the tab switch.

## Polling loop

`Program.cs` logs in once, then loops forever: scrape → diff against the last snapshot → email if changed → save the new snapshot → sleep 2 minutes → repeat. A failed iteration (network blip, page hiccup) is logged and skipped rather than crashing the whole process.

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

### State and deduplication

The full snapshot of the last poll's results (not just keys) is stored in `data/research-state.json`, next to the build output alongside `chrome-profile/`. Each poll diffs the current scrape against that snapshot, keyed on `Symbol + RecoPrice`:

- Keys present now but not in the snapshot → **added**
- Keys in the snapshot but not present now → **removed**

Both directions trigger an email; the snapshot is only overwritten when there's an actual change. Delete `data/research-state.json` to reset — the next poll's items will all show up as "added".
