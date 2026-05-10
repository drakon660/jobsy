# jobsy

Fast C# CLI scraper using Playwright for:

- `https://justjoin.it/job-offers/remote/net`

### Requirements

- .NET SDK 8+

### Run

```bash
dotnet run -- job-offers.json
```

If no argument is provided, output is saved to:

- `job-offers-remote-net.json`

### Build

```bash
dotnet build
```

### Output

The CLI writes JSON with:

- source URL
- scrape timestamp (UTC)
- offers count
- offers list (`title`, `company`, `location`, `url`)
