using System.Diagnostics;

namespace AichanToolbox.Core;

internal sealed record TargetSizeAttempt(int Number, int Quality, long Size, TimeSpan Elapsed, string Path);

internal sealed record TargetSizeResult(
    string OutputPath,
    int Quality,
    long Size,
    bool MetTarget,
    IReadOnlyList<TargetSizeAttempt> Attempts);

internal sealed class TargetSizeOptimizer
{
    private readonly ImageEngine _engine;

    public TargetSizeOptimizer(ImageEngine engine) => _engine = engine;

    public async Task<TargetSizeResult> OptimizeAsync(
        string sourcePath,
        int width,
        int height,
        long targetBytes,
        int startingQuality = 90,
        int span = 5,
        int minimumQuality = 50,
        int maximumAttempts = 5,
        int? hintedQuality = null,
        bool autoGrayscale = true,
        int descreenLevel = 0,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("目标体积测试源图片不存在。", sourcePath);
        if (targetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(targetBytes));
        startingQuality = Math.Clamp(startingQuality, 20, 99);
        span = Math.Clamp(span, 1, 80);
        minimumQuality = Math.Clamp(minimumQuality, 20, startingQuality);
        maximumAttempts = Math.Clamp(maximumAttempts, 2, 12);
        var maximumQuality = Math.Min(100, startingQuality + span);
        var attempts = new Dictionary<int, TargetSizeAttempt>();
        string? prepared = null;

        async Task<TargetSizeAttempt> TryQualityAsync(int quality)
        {
            quality = Math.Clamp(quality, minimumQuality, maximumQuality);
            if (attempts.TryGetValue(quality, out var cached)) return cached;
            var timer = Stopwatch.StartNew();
            var path = await _engine.EncodePreparedJpegAsync(prepared!, quality, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            var attempt = new TargetSizeAttempt(attempts.Count + 1, quality, new FileInfo(path).Length, timer.Elapsed, path);
            attempts.Add(quality, attempt);
            return attempt;
        }

        try
        {
            prepared = await _engine.PrepareJpegSourceAsync(
                sourcePath,
                width,
                height,
                autoGrayscale,
                descreenLevel,
                cancellationToken).ConfigureAwait(false);
            var first = await TryQualityAsync(startingQuality).ConfigureAwait(false);
            if (first.Size <= targetBytes && startingQuality < maximumQuality && attempts.Count < maximumAttempts)
            {
                await TryQualityAsync(maximumQuality).ConfigureAwait(false);
            }
            else if (first.Size > targetBytes && attempts.Count < maximumAttempts)
            {
                var ratio = first.Size / (double)targetBytes;
                var adaptiveDrop = ratio switch
                {
                    <= 1.15 => span,
                    <= 1.50 => Math.Max(span, 10),
                    <= 2.00 => Math.Max(span, 20),
                    _ => Math.Max(span, 30)
                };
                var lowerProbe = hintedQuality is >= 20 and < 100
                    ? Math.Clamp(hintedQuality.Value, minimumQuality, startingQuality - 1)
                    : Math.Max(minimumQuality, startingQuality - adaptiveDrop);
                var lower = await TryQualityAsync(lowerProbe).ConfigureAwait(false);
                if (lower.Size > targetBytes
                    && lowerProbe > minimumQuality
                    && attempts.Count < maximumAttempts)
                {
                    // JPEG size is locally close to exponential in quality. Two real
                    // over-target samples let us jump close to the boundary without
                    // trusting the estimate as a result: the predicted quality is
                    // still encoded and measured, with a two-point safety offset.
                    var predicted = ExtrapolateBelowTarget(lower, first, targetBytes, minimumQuality);
                    if (!attempts.ContainsKey(predicted))
                        await TryQualityAsync(predicted).ConfigureAwait(false);
                }
            }

            while (attempts.Count < maximumAttempts)
            {
                var fitting = attempts.Values
                    .Where(value => value.Size <= targetBytes)
                    .OrderByDescending(value => value.Quality)
                    .FirstOrDefault();
                if (fitting is null)
                {
                    if (!attempts.ContainsKey(minimumQuality))
                    {
                        await TryQualityAsync(minimumQuality).ConfigureAwait(false);
                        continue;
                    }
                    break;
                }
                if (fitting.Quality >= maximumQuality) break;

                var failing = attempts.Values
                    .Where(value => value.Quality > fitting.Quality && value.Size > targetBytes)
                    .OrderBy(value => value.Quality)
                    .FirstOrDefault();
                if (failing is null)
                {
                    if (!attempts.ContainsKey(maximumQuality))
                    {
                        await TryQualityAsync(maximumQuality).ConfigureAwait(false);
                        continue;
                    }
                    break;
                }
                if (failing.Quality - fitting.Quality <= 1) break;

                var quality = InterpolateQuality(fitting, failing, targetBytes);
                if (attempts.ContainsKey(quality)) quality = (fitting.Quality + failing.Quality) / 2;
                if (quality <= fitting.Quality || quality >= failing.Quality) break;
                await TryQualityAsync(quality).ConfigureAwait(false);
            }

            var selected = attempts.Values
                .Where(value => value.Size <= targetBytes)
                .OrderByDescending(value => value.Quality)
                .FirstOrDefault()
                ?? attempts.Values.OrderBy(value => value.Size).First();
            foreach (var attempt in attempts.Values.Where(value => !value.Path.Equals(selected.Path, StringComparison.OrdinalIgnoreCase)))
                TryDelete(attempt.Path);
            return new TargetSizeResult(
                selected.Path,
                selected.Quality,
                selected.Size,
                selected.Size <= targetBytes,
                attempts.Values.OrderBy(value => value.Number).ToList());
        }
        catch
        {
            foreach (var attempt in attempts.Values) TryDelete(attempt.Path);
            throw;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(prepared)) TryDelete(prepared);
        }
    }

    private static int InterpolateQuality(TargetSizeAttempt fitting, TargetSizeAttempt failing, long targetBytes)
    {
        var fitLog = Math.Log(Math.Max(1, fitting.Size));
        var failLog = Math.Log(Math.Max(1, failing.Size));
        if (Math.Abs(failLog - fitLog) < 0.000001)
            return (fitting.Quality + failing.Quality) / 2;
        var position = (Math.Log(targetBytes) - fitLog) / (failLog - fitLog);
        var estimated = fitting.Quality + position * (failing.Quality - fitting.Quality);
        return Math.Clamp((int)Math.Floor(estimated), fitting.Quality + 1, failing.Quality - 1);
    }

    private static int ExtrapolateBelowTarget(
        TargetSizeAttempt lowerQuality,
        TargetSizeAttempt higherQuality,
        long targetBytes,
        int minimumQuality)
    {
        var qualityDistance = higherQuality.Quality - lowerQuality.Quality;
        var sizeDistance = Math.Log(Math.Max(1, higherQuality.Size)) - Math.Log(Math.Max(1, lowerQuality.Size));
        if (qualityDistance <= 0 || sizeDistance <= 0.000001)
            return Math.Max(minimumQuality, lowerQuality.Quality - 5);

        var slope = sizeDistance / qualityDistance;
        var estimated = lowerQuality.Quality
            + (Math.Log(targetBytes) - Math.Log(Math.Max(1, lowerQuality.Size))) / slope;
        var conservative = (int)Math.Floor(estimated) - 2;
        return Math.Clamp(conservative, minimumQuality, lowerQuality.Quality - 1);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
