using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Serilog;

public class JustJoinItJobDownloader
{
    private const string TargetUrl = "https://justjoin.it/job-offers/remote/net";

    private readonly HashSet<string> _uniqueLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _networkCapturedSlugs = new(StringComparer.OrdinalIgnoreCase);

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
            Headless = true
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

        page.Response += async (_, response) =>
        {
            try
            {
                var url = response.Url;

                if (response.Status != 200)
                {
                    return;
                }

                var isRsc = url.Contains("justjoin.it/job-offers", StringComparison.OrdinalIgnoreCase)
                    && (response.Request.Headers.ContainsKey("rsc")
                        || url.Contains("_rsc", StringComparison.OrdinalIgnoreCase));

                var isApi = url.Contains("api.justjoin.it", StringComparison.OrdinalIgnoreCase)
                    && url.Contains("offer", StringComparison.OrdinalIgnoreCase);

                if (!isRsc && !isApi)
                {
                    return;
                }

                var body = await response.TextAsync();
                if (string.IsNullOrEmpty(body) || body.Length < 50)
                {
                    return;
                }

                var patterns = new[]
                {
                    @"""slug""\s*:\s*""([^""]+)""",
                    @"\\""slug\\""\s*:\s*\\""([^""\\]+)\\""",
                    @"\\u0022slug\\u0022\s*:\s*\\u0022([^\\]+)\\u0022",
                };

                var beforeCount = _networkCapturedSlugs.Count;
                foreach (var pattern in patterns)
                {
                    foreach (Match match in Regex.Matches(body, pattern))
                    {
                        var slug = match.Groups[1].Value;
                        if (slug.Length > 5 && !slug.Contains(' '))
                        {
                            _networkCapturedSlugs.Add(slug);
                            _uniqueLinks.Add($"https://justjoin.it/job-offer/{slug}");
                        }
                    }
                }

                var newCount = _networkCapturedSlugs.Count - beforeCount;
                if (newCount > 0)
                {
                    Log.Information("Network response captured {NewCount} new slugs (total unique: {Total}) from {Type}",
                        newCount, _networkCapturedSlugs.Count, isRsc ? "RSC" : "API");
                }
            }
            catch
            {
                // Ignore errors from response interception
            }
        };

        Log.Information("Navigating to {TargetUrl}", TargetUrl);
        await page.GotoAsync(TargetUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60_000
        });

        await page.WaitForTimeoutAsync(5_000);

        Log.Information("Starting auto-scroll to load lazy job cards");

        var initialVisibleCount = await CollectVisibleLinksAsync(page);
        var reportedOffersCount = await ExtractReportedOffersCountAsync(page);

        if (reportedOffersCount is not null)
        {
            Log.Information("Page initially reports {ReportedOffersCount} offers, network captured {NetworkCount} so far",
                reportedOffersCount, _networkCapturedSlugs.Count);
        }

        var loadedPagesCount = _uniqueLinks.Count > 0 ? 1 : 0;
        var roundsWithoutProgress = 0;
        const int maxStableRounds = 15;
        var effectiveTarget = reportedOffersCount ?? 0;
        var maxRounds = effectiveTarget > 0
            ? Math.Max(300, (int)Math.Ceiling(effectiveTarget / 8.0) * 5)
            : 300;

        for (var round = 0; round < maxRounds && roundsWithoutProgress < maxStableRounds; round++)
        {
            var previousMetrics = await GetScrollMetricsAsync(page);
            var previousUniqueLinksCount = _uniqueLinks.Count;

            var trailingTarget = effectiveTarget > 0 && previousUniqueLinksCount < effectiveTarget;

            await page.Mouse.WheelAsync(0, 1600);

            var waitAttempts = trailingTarget ? 5 : 8;
            var waitDelayMs = trailingTarget && loadedPagesCount >= 10 ? 3_000 : 750;

            for (var attempt = 0; attempt < waitAttempts; attempt++)
            {
                await page.WaitForTimeoutAsync(waitDelayMs);
                await CollectVisibleLinksAsync(page);

                var metrics = await GetScrollMetricsAsync(page);
                if (_uniqueLinks.Count > previousUniqueLinksCount || metrics.ScrollHeight > previousMetrics.ScrollHeight)
                {
                    break;
                }
            }

            var currentCount = await CollectVisibleLinksAsync(page);
            var currentMetrics = await GetScrollMetricsAsync(page);
            var currentUniqueLinksCount = _uniqueLinks.Count;
            var foundNewLinks = currentUniqueLinksCount > previousUniqueLinksCount;
            var scrolledFurther = currentMetrics.ScrollTop > previousMetrics.ScrollTop;
            var pageExtended = currentMetrics.ScrollHeight > previousMetrics.ScrollHeight;

            if (foundNewLinks)
            {
                loadedPagesCount++;
                roundsWithoutProgress = 0;
            }
            else if (scrolledFurther || pageExtended || !currentMetrics.AtBottom)
            {
                roundsWithoutProgress = 0;
            }
            else
            {
                roundsWithoutProgress++;

                if (trailingTarget && roundsWithoutProgress >= 3 && currentMetrics.AtBottom)
                {
                    Log.Information("Scroll reset attempt (unique: {Count}, target: {Target})",
                        currentUniqueLinksCount, effectiveTarget);
                    await page.Mouse.WheelAsync(0, -5000);
                    await page.WaitForTimeoutAsync(2_000);
                    await page.Mouse.WheelAsync(0, 8000);
                    await page.WaitForTimeoutAsync(3_000);
                    await CollectVisibleLinksAsync(page);

                    if (_uniqueLinks.Count > currentUniqueLinksCount)
                    {
                        roundsWithoutProgress = 0;
                    }
                }
            }

            Log.Information(
                "Round {Round}: top={ScrollTop}, height={ScrollHeight}, visible={CardsCount}, unique={UniqueLinksCount}, network={NetworkCount}, pages={LoadedPagesCount}, stale={StableRounds}, target={Target}",
                round + 1,
                currentMetrics.ScrollTop,
                currentMetrics.ScrollHeight,
                currentCount,
                currentUniqueLinksCount,
                _networkCapturedSlugs.Count,
                loadedPagesCount,
                roundsWithoutProgress,
                effectiveTarget
            );

            if (effectiveTarget > 0 && currentUniqueLinksCount >= effectiveTarget)
            {
                Log.Information(
                    "Stopping scroll: collected {Count} >= target {Target}",
                    currentUniqueLinksCount, effectiveTarget
                );
                break;
            }
        }

        await CollectVisibleLinksAsync(page);

        Log.Information(
            "Auto-scroll finished, initial visible: {InitialVisibleCards}, unique links: {UniqueLinksCount}, network slugs: {NetworkCount}",
            initialVisibleCount,
            _uniqueLinks.Count,
            _networkCapturedSlugs.Count
        );

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
            PagesCount: loadedPagesCount,
            Offers: offers
        );

        if (payload.Count == 0)
        {
            Log.Warning("No offers were extracted. The site DOM may have changed or content may not be visible for this session.");
        }

        Log.Information("Extracted {OffersCount} offers", payload.Count);

        if (payload.ReportedOffersCount is not null)
        {
            Log.Information("Page reported {ReportedOffersCount} offers", payload.ReportedOffersCount);
        }

        Log.Information("Loaded {PagesCount} scroll pages", payload.PagesCount);

        return payload;
    }

    private async Task<int?> ExtractReportedOffersCountAsync(IPage page)
    {
        var offersSummaryText = await page.EvaluateAsync<string?>("""
        () => {
            for (const h1 of document.querySelectorAll('h1')) {
                const text = h1.textContent?.trim() ?? '';
                if (/\b\d{2,}\b.*\b(job offers|jobs|offers)\b/i.test(text)) {
                    return text;
                }
            }

            const candidates = [
                ...document.querySelectorAll('h2, h3, span, p')
            ];

            for (const candidate of candidates) {
                const text = candidate.textContent?.trim() ?? '';
                if (/\b\d{2,}\b.*\b(job offers|jobs|offers)\b/i.test(text) && text.length < 200) {
                    return text;
                }
            }

            return null;
        }
        """);

        if (!string.IsNullOrWhiteSpace(offersSummaryText))
        {
            var summaryMatch = Regex.Match(offersSummaryText, @"\d+");
            if (summaryMatch.Success)
            {
                return int.Parse(summaryMatch.Value);
            }
        }

        var pageContent = await page.ContentAsync();
        var embeddedDataMatch = Regex.Match(
            pageContent,
            @"\\""totalItems\\""\s*:\s*(\d+)",
            RegexOptions.IgnoreCase
        );

        if (embeddedDataMatch.Success)
        {
            return int.Parse(embeddedDataMatch.Groups[1].Value);
        }

        return null;
    }

    private async Task<int> CollectVisibleLinksAsync(IPage page)
    {
        var visibleLinksJson = await page.EvaluateAsync<string>("""
        () => {
            const links = [];

            for (const card of document.querySelectorAll('a.offer-card[href]')) {
                const url = card.getAttribute('href') || '';

                if (!url) {
                    continue;
                }

                links.push(new URL(url, window.location.origin).toString());
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

    private static async Task<ScrollMetrics> GetScrollMetricsAsync(IPage page)
    {
        var metricsJson = await page.EvaluateAsync<string>("""
        () => {
            const scrollingElement = document.scrollingElement || document.documentElement || document.body;
            const scrollTop = Math.round(scrollingElement.scrollTop);
            const scrollHeight = Math.round(scrollingElement.scrollHeight);
            const clientHeight = Math.round(window.innerHeight || scrollingElement.clientHeight || 0);
            const atBottom = scrollTop + clientHeight >= scrollHeight - 5;

            return JSON.stringify({
                scrollTop,
                scrollHeight,
                clientHeight,
                atBottom
            });
        }
        """);

        return JsonSerializer.Deserialize<ScrollMetrics>(metricsJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })
            ?? new ScrollMetrics(0, 0, 0, false);
    }
}

public sealed record ScrapeResult(
    string SourceUrl,
    DateTime ScrapedAtUtc,
    int Count,
    int? ReportedOffersCount,
    int PagesCount,
    List<JobOffer> Offers
);

public sealed record JobOffer(
    string Title,
    string? Company,
    string Location,
    string Url
);

public sealed record ScrollMetrics(
    int ScrollTop,
    int ScrollHeight,
    int ClientHeight,
    bool AtBottom
);
