using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AichanToolbox.Core;

internal static class StartupTelemetry
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly double ProcessAgeAtFirstMarkMs = ReadProcessAgeMilliseconds();
    private static readonly ConcurrentDictionary<string, double> Points = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, double> Metrics = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private static string _webViewProfile = "unknown";

    public static void Mark(string name)
        => Points[name] = Math.Round(ProcessAgeAtFirstMarkMs + Clock.Elapsed.TotalMilliseconds, 2);

    public static void SetMetric(string name, double milliseconds)
    {
        if (double.IsFinite(milliseconds) && milliseconds >= 0)
            Metrics[name] = Math.Round(milliseconds, 2);
    }

    public static void SetWebViewProfile(bool existed)
        => _webViewProfile = existed ? "existing" : "new";

    public static void FlushInBackground(string reason)
        => _ = Task.Run(() => FlushAsync(reason));

    private static async Task FlushAsync(string reason)
    {
        await WriteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AichanToolbox",
                "Logs");
            Directory.CreateDirectory(directory);
            var payload = new
            {
                generatedAtUtc = DateTime.UtcNow,
                reason,
                processId = Environment.ProcessId,
                webViewProfile = _webViewProfile,
                points = Points.OrderBy(value => value.Value).ToDictionary(value => value.Key, value => value.Value),
                metrics = Metrics.OrderBy(value => value.Key).ToDictionary(value => value.Key, value => value.Value)
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                Path.Combine(directory, "startup-latest.json"),
                json,
                new UTF8Encoding(false)).ConfigureAwait(false);
        }
        catch
        {
            // Startup telemetry must never affect application startup or image processing.
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private static double ReadProcessAgeMilliseconds()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return Math.Max(0, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds);
        }
        catch
        {
            return 0;
        }
    }
}
