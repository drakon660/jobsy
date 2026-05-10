using System.Text.Json;
using Serilog;

var outputPath = args.Length > 0 ? args[0] : "job-offers-remote-net.json";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var downloader = new NoFluffJobsDownloader();
    var result = await downloader.DownloadAsync();

    await File.WriteAllTextAsync(
        outputPath,
        JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        })
    );

    Log.Information("Saved {OffersCount} offers to {OutputPath}", result.Count, outputPath);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled error while scraping offers");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
