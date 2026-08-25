using System.Diagnostics;
using System.Globalization;
using AichanToolbox.Core;

if (args.Length is not 4 and not 8 and not 9)
    throw new InvalidOperationException("用法：<项目根目录> <源图片> <目标MB> <输出路径> [起始画质 跨幅 下限 最大尝试次数 [缩放百分比]]");

var projectRoot = Path.GetFullPath(args[0]);
var sourcePath = Path.GetFullPath(args[1]);
var targetMb = double.Parse(args[2], CultureInfo.InvariantCulture);
var outputPath = Path.GetFullPath(args[3]);
var startingQuality = args.Length >= 8 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 90;
var span = args.Length >= 8 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 5;
var minimumQuality = args.Length >= 8 ? int.Parse(args[6], CultureInfo.InvariantCulture) : 50;
var maximumAttempts = args.Length >= 8 ? int.Parse(args[7], CultureInfo.InvariantCulture) : 5;
var scalePercent = args.Length == 9 ? int.Parse(args[8], CultureInfo.InvariantCulture) : 100;
var temporaryRoot = Path.Combine(Path.GetTempPath(), "AichanTargetSizeProbe", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);

try
{
    var ffmpeg = Path.Combine(projectRoot, "vendor", "ffmpeg", "ffmpeg.exe");
    var jpegli = Path.Combine(projectRoot, "vendor", "jxl-v0.11.2-win-x64", "bin", "cjpegli.exe");
    var engine = new ImageEngine(ffmpeg, jpegli, temporaryRoot);
    var sourceDimensions = engine.ReadDimensions(sourcePath);
    var dimensions = (
        Width: Math.Max(2, (int)Math.Truncate(sourceDimensions.Width * Math.Clamp(scalePercent, 20, 100) / 100d)),
        Height: Math.Max(2, (int)Math.Truncate(sourceDimensions.Height * Math.Clamp(scalePercent, 20, 100) / 100d)));
    var optimizer = new TargetSizeOptimizer(engine);
    var total = Stopwatch.StartNew();
    var result = await optimizer.OptimizeAsync(
        sourcePath,
        dimensions.Width,
        dimensions.Height,
        (long)Math.Floor(targetMb * 1024 * 1024),
        startingQuality,
        span,
        minimumQuality,
        maximumAttempts);
    total.Stop();

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.Copy(result.OutputPath, outputPath, true);
    Console.WriteLine($"SOURCE={sourcePath}");
    Console.WriteLine($"DIMENSIONS={dimensions.Width}x{dimensions.Height} scale={scalePercent}% source={sourceDimensions.Width}x{sourceDimensions.Height}");
    Console.WriteLine($"ORIGINAL_BYTES={new FileInfo(sourcePath).Length}");
    Console.WriteLine($"TARGET_BYTES={(long)Math.Floor(targetMb * 1024 * 1024)}");
    foreach (var attempt in result.Attempts)
        Console.WriteLine($"ATTEMPT number={attempt.Number} quality={attempt.Quality} bytes={attempt.Size} elapsed_ms={attempt.Elapsed.TotalMilliseconds:F0}");
    Console.WriteLine($"RESULT met={result.MetTarget.ToString().ToLowerInvariant()} quality={result.Quality} bytes={result.Size} attempts={result.Attempts.Count} total_ms={total.Elapsed.TotalMilliseconds:F0}");
    Console.WriteLine($"OUTPUT={outputPath}");
}
finally
{
    try { Directory.Delete(temporaryRoot, true); } catch { }
}
