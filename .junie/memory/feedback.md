[2026-04-02 22:12] - Updated by Junie
{
    "TYPE": "preference",
    "CATEGORY": "Browser viewport size",
    "EXPECTATION": "The browser should start with a large resolution so job listings render fully.",
    "NEW INSTRUCTION": "WHEN launching Playwright context THEN set viewport to at least 1920x1080"
}

[2026-04-02 22:14] - Updated by Junie
{
    "TYPE": "correction",
    "CATEGORY": "Infinite scroll",
    "EXPECTATION": "The scraper should auto-scroll to load more job cards and collect all links.",
    "NEW INSTRUCTION": "WHEN job list loads content on scroll THEN scroll to bottom until no new cards appear"
}

[2026-04-02 22:30] - Updated by Junie
{
    "TYPE": "correction",
    "CATEGORY": "Early scroll stop",
    "EXPECTATION": "The scraper should keep scrolling/loading beyond 20 pages until it collects all ~565 offers.",
    "NEW INSTRUCTION": "WHEN reported offers count exceeds collected links THEN continue scrolling with longer waits and higher max rounds"
}

[2026-04-02 22:45] - Updated by Junie
{
    "TYPE": "correction",
    "CATEGORY": "Early stop on scroll",
    "EXPECTATION": "The scraper should iterate beyond 20 pages and keep loading until it collects all ~565 offers, possibly by waiting longer between scrolls.",
    "NEW INSTRUCTION": "WHEN collected links < reported offers after 20 pages THEN keep scrolling with 3-5s waits and maxRounds >= 200"
}

[2026-04-03 07:20] - Updated by Junie
{
    "TYPE": "correction",
    "CATEGORY": "Offer count discrepancy",
    "EXPECTATION": "The scraper should match the current page-reported total (656) and collect them all.",
    "NEW INSTRUCTION": "WHEN page shows reported offers > collected links THEN keep scrolling until collected equals reported"
}

[2026-04-03 07:25] - Updated by Junie
{
    "TYPE": "correction",
    "CATEGORY": "Offer count mismatch",
    "EXPECTATION": "Scraper should collect all offers shown on the page (565), not stop around 229.",
    "NEW INSTRUCTION": "WHEN embedded totalItems < visible on-page count THEN use the higher visible count as target"
}

[2026-04-03 08:28] - Updated by Junie
{
    "TYPE": "correction",
    "CATEGORY": "Scroll count mismatch",
    "EXPECTATION": "Scraper should keep scrolling until it collects all 565 offers visible on the page, not stop around 229.",
    "NEW INSTRUCTION": "WHEN collected links < visible on-page total THEN keep scrolling until counts match"
}

