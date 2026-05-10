using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Serilog;

public class NoFluffJobsDownloader
{
    private const string TargetUrl = "https://nofluffjobs.com/pl/.NET";

    private readonly HashSet<string> _uniqueLinks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ScrapeResult> DownloadAsync()
    {
        Log.Information("Starting scraper for {TargetUrl}", TargetUrl);

        var browserInstallExitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        if (browserInstallExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Playwright browser installation failed with exit code {browserInstallExitCode}");
        }

        Log.Information("Playwright browser ready");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1920,
                Height = 1080
            }
        });
        var page = await context.NewPageAsync();

        Log.Information("Navigating to {TargetUrl}", TargetUrl);
        await page.GotoAsync(TargetUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60_000
        });

        await page.WaitForTimeoutAsync(3_000);

        // Dismiss cookie consent if present
        try
        {
            var cookieButton = page.Locator("button:has-text('Akceptuję'), button:has-text('Accept'), #onetrust-accept-btn-handler");
            if (await cookieButton.CountAsync() > 0)
            {
                await cookieButton.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                await page.WaitForTimeoutAsync(1_000);
                Log.Information("Dismissed cookie consent");
            }
        }
        catch
        {
            // No cookie banner or already dismissed
        }

        var reportedOffersCount = await ExtractReportedOffersCountAsync(page);
        if (reportedOffersCount is not null)
        {
            Log.Information("Page reports {ReportedOffersCount} offers", reportedOffersCount);
        }

        await CollectVisibleLinksAsync(page);
        Log.Information("Initial page loaded {Count} unique offers", _uniqueLinks.Count);

        var pagesCount = 1;
        var maxPages = 100;

        for (var pageNum = 2; pageNum <= maxPages; pageNum++)
        {
            var showMoreButton = page.Locator("button:has-text('Pokaż kolejne oferty')");

            if (await showMoreButton.CountAsync() == 0)
            {
                Log.Information("No more 'Pokaż kolejne oferty' button found, stopping");
                break;
            }

            var previousCount = _uniqueLinks.Count;

            try
            {
                await showMoreButton.First.ScrollIntoViewIfNeededAsync();
                await page.WaitForTimeoutAsync(500);
                await showMoreButton.First.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to click 'Pokaż kolejne oferty': {Message}", ex.Message);
                break;
            }

            // Wait for new content to load
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await page.WaitForTimeoutAsync(1_000);
                await CollectVisibleLinksAsync(page);

                if (_uniqueLinks.Count > previousCount)
                {
                    break;
                }
            }

            await CollectVisibleLinksAsync(page);
            pagesCount++;

            var newOffers = _uniqueLinks.Count - previousCount;
            Log.Information("Page {PageNum}: loaded {NewOffers} new offers, total unique: {Total}",
                pageNum, newOffers, _uniqueLinks.Count);

            if (newOffers == 0)
            {
                Log.Information("No new offers loaded after click, stopping");
                break;
            }

            if (reportedOffersCount.HasValue && _uniqueLinks.Count >= reportedOffersCount.Value)
            {
                Log.Information("Collected {Count} >= reported {Target}, stopping",
                    _uniqueLinks.Count, reportedOffersCount.Value);
                break;
            }
        }

        var offers = _uniqueLinks
            .Select(url => new JobOffer(
                Title: "N/A",
                Company: null,
                Location: "Remote",
                Url: url
            ))
            .ToList();

        var payload = new ScrapeResult(
            SourceUrl: TargetUrl,
            ScrapedAtUtc: DateTime.UtcNow,
            Count: offers.Count,
            ReportedOffersCount: reportedOffersCount,
            PagesCount: pagesCount,
            Offers: offers
        );

        if (payload.Count == 0)
        {
            Log.Warning("No offers were extracted. The site DOM may have changed.");
        }

        Log.Information("Extracted {OffersCount} offers from {PagesCount} pages",
            payload.Count, payload.PagesCount);

        return payload;
    }

    private async Task<int?> ExtractReportedOffersCountAsync(IPage page)
    {
        var countText = await page.EvaluateAsync<string?>("""
        () => {
            for (const el of document.querySelectorAll('h1, h2, h3, span, p, div')) {
                const text = el.textContent?.trim() ?? '';
                if (/\b\d{2,}\b.*\b(ofert|jobs|offers)\b/i.test(text) && text.length < 200) {
                    return text;
                }
            }
            return null;
        }
        """);

        if (!string.IsNullOrWhiteSpace(countText))
        {
            var match = Regex.Match(countText, @"\d+");
            if (match.Success)
            {
                return int.Parse(match.Value);
            }
        }

        return null;
    }

    private async Task<int> CollectVisibleLinksAsync(IPage page)
    {
        var visibleLinksJson = await page.EvaluateAsync<string>("""
        () => {
            const links = [];
            const selectors = [
                'a[href*="/job/"]',
                'a[href*="/pl/job/"]',
                'a.posting-list-item[href]',
                'a.list-item[href]',
                'nfj-postings-list a[href]',
                'a[data-cy="posting"]',
                '.posting-list a[href]'
            ];

            const seen = new Set();
            for (const selector of selectors) {
                for (const el of document.querySelectorAll(selector)) {
                    const href = el.getAttribute('href') || '';
                    if (!href || seen.has(href)) continue;
                    seen.add(href);
                    links.push(new URL(href, window.location.origin).toString());
                }
            }

            return JSON.stringify(links);
        }
        """);

        var visibleLinks = JsonSerializer.Deserialize<List<string>>(visibleLinksJson ?? "[]") ?? [];
        foreach (var link in visibleLinks)
        {
            _uniqueLinks.Add(link);
        }

        return visibleLinks.Count;
    }
}
