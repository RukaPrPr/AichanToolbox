using System.Diagnostics;
using System.Text;
using NetVips;
using VipsImage = NetVips.Image;

namespace AichanToolbox.Core;

internal sealed class ImageEngine
{
    private readonly string _ffmpegPath;
    private readonly IJpegEncoder _jpegEncoder;
    private readonly string _temporaryRoot;
    private readonly object _vipsInitializationGate = new();
    private bool _vipsReady;
    private Exception? _vipsInitializationError;
    private int _jpegliFirstCallStarted;

    public ImageEngine(string ffmpegPath, string jpegliPath, string temporaryRoot)
    {
        _ffmpegPath = ffmpegPath;
        _jpegEncoder = new JpegliProcessEncoder(jpegliPath);
        _temporaryRoot = temporaryRoot;
        Directory.CreateDirectory(_temporaryRoot);
    }

    public void ValidateDependencies()
    {
        if (!_jpegEncoder.IsAvailable)
            throw new InvalidOperationException("高画质编码器 cjpegli.exe 缺失。");
        EnsureVipsInitialized();
        DisableOperationCache();
    }

    public void ConfigureConcurrency(int fileParallelism)
    {
        EnsureVipsInitialized();
        var logicalProcessors = Math.Max(1, Environment.ProcessorCount);
        NetVips.NetVips.Concurrency = Math.Clamp(logicalProcessors / Math.Max(1, fileParallelism), 1, 4);
    }

    public (int Width, int Height) ReadDimensions(string input)
    {
        var metadata = ImageMetadataReader.ReadDimensions(input);
        if (metadata.Width > 0 && metadata.Height > 0) return metadata;

        EnsureVipsInitialized();
        try
        {
            using var source = VipsImage.NewFromFile(input, access: Enums.Access.Sequential, failOn: Enums.FailOn.Error, revalidate: true);
            using var oriented = source.Autorot();
            return (oriented.Width, oriented.Height);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"无法读取图片尺寸：{Path.GetFileName(input)}。", exception);
        }
    }

    public async Task<string> RenderAsync(
        string input,
        string targetExtension,
        int targetWidth,
        int targetHeight,
        int jpegQuality,
        bool autoGrayscale,
        int descreenLevel,
        CancellationToken cancellationToken)
    {
        ValidateDependencies();
        cancellationToken.ThrowIfCancellationRequested();
        var extension = NormalizeExtension(targetExtension, input);
        var output = TemporaryPath(extension);
        var isJpeg = extension is ".jpg" or ".jpeg";
        string? fallback = null;

        try
        {
            try
            {
                if (isJpeg)
                {
                    await EncodeJpegFromVipsAsync(
                        input,
                        output,
                        targetWidth,
                        targetHeight,
                        jpegQuality,
                        autoGrayscale,
                        descreenLevel,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Run(
                        () => PrepareWithVips(input, output, targetWidth, targetHeight, false, autoGrayscale, descreenLevel, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception firstError) when (IsVipsFailure(firstError) && File.Exists(_ffmpegPath))
            {
                fallback = TemporaryPath(".png");
                await DecodeFallbackAsync(input, fallback, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (isJpeg)
                    {
                        await EncodeJpegFromVipsAsync(
                            fallback,
                            output,
                            targetWidth,
                            targetHeight,
                            jpegQuality,
                            autoGrayscale,
                            descreenLevel,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Run(
                            () => PrepareWithVips(fallback, output, targetWidth, targetHeight, false, autoGrayscale, descreenLevel, cancellationToken),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception fallbackError)
                {
                    throw new InvalidOperationException(
                        "无法解码此图片。libvips：" + firstError.Message + "；FFmpeg 回退：" + fallbackError.Message,
                        fallbackError);
                }
            }
            catch (Exception firstError) when (IsVipsFailure(firstError))
            {
                throw MissingCompatibilityDecoder(input, firstError);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(output)) throw new InvalidOperationException("图片处理没有生成输出文件。");
            return output;
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(fallback)) TryDelete(fallback);
        }
    }

    internal async Task<string> PrepareJpegSourceAsync(
        string input,
        int targetWidth,
        int targetHeight,
        bool autoGrayscale,
        int descreenLevel,
        CancellationToken cancellationToken)
    {
        ValidateDependencies();
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = TemporaryPath(".png");
        string? fallback = null;
        try
        {
            try
            {
                await Task.Run(
                    () => PrepareWithVips(input, prepared, targetWidth, targetHeight, true, autoGrayscale, descreenLevel, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception firstError) when (IsVipsFailure(firstError) && File.Exists(_ffmpegPath))
            {
                fallback = TemporaryPath(".png");
                await DecodeFallbackAsync(input, fallback, cancellationToken).ConfigureAwait(false);
                try
                {
                    await Task.Run(
                        () => PrepareWithVips(fallback, prepared, targetWidth, targetHeight, true, autoGrayscale, descreenLevel, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception fallbackError)
                {
                    throw new InvalidOperationException(
                        "无法解码此图片。libvips：" + firstError.Message + "；FFmpeg 回退：" + fallbackError.Message,
                        fallbackError);
                }
            }
            catch (Exception firstError) when (IsVipsFailure(firstError))
            {
                throw MissingCompatibilityDecoder(input, firstError);
            }

            return prepared;
        }
        catch
        {
            TryDelete(prepared);
            throw;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(fallback)) TryDelete(fallback);
        }
    }

    internal async Task<string> EncodePreparedJpegAsync(
        string preparedSource,
        int jpegQuality,
        CancellationToken cancellationToken)
    {
        ValidateDependencies();
        if (!File.Exists(preparedSource)) throw new FileNotFoundException("无损工作底图不存在。", preparedSource);
        var output = TemporaryPath(".jpg");
        try
        {
            await EncodeJpegliAsync(preparedSource, output, jpegQuality, cancellationToken).ConfigureAwait(false);
            return output;
        }
        catch
        {
            TryDelete(output);
            throw;
        }
    }

    private static void PrepareWithVips(
        string input,
        string output,
        int targetWidth,
        int targetHeight,
        bool flattenForJpeg,
        bool autoGrayscale,
        int descreenLevel,
        CancellationToken cancellationToken)
    {
        WithPreparedImage(
            input,
            targetWidth,
            targetHeight,
            flattenForJpeg,
            autoGrayscale,
            descreenLevel,
            cancellationToken,
            current =>
            {
                if (Path.GetExtension(output).Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    var keep = current.Bands == 1 ? Enums.ForeignKeep.None : Enums.ForeignKeep.All;
                    current.Pngsave(output, compression: 1, keep: keep);
                }
                else
                {
                    current.WriteToFile(output, new VOption { { "keep", Enums.ForeignKeep.All } });
                }
            });
    }

    private static void WithPreparedImage(
        string input,
        int targetWidth,
        int targetHeight,
        bool flattenForJpeg,
        bool autoGrayscale,
        int descreenLevel,
        CancellationToken cancellationToken,
        Action<VipsImage> consume)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var access = flattenForJpeg && autoGrayscale ? Enums.Access.Random : Enums.Access.Sequential;
        using var source = VipsImage.NewFromFile(input, access: access, failOn: Enums.FailOn.Error, revalidate: true);
        using var oriented = source.Autorot();
        VipsImage current = oriented;
        var generated = new List<VipsImage>();
        try
        {
            if (flattenForJpeg && current.HasAlpha())
            {
                var white = WhiteLevel(current.Format);
                current = Track(current.Flatten(background: new[] { white, white, white }), generated);
            }

            var width = Math.Max(1, targetWidth);
            var height = Math.Max(1, targetHeight);
            if (current.Width != width || current.Height != height)
            {
                var horizontalScale = width / (double)current.Width;
                var verticalScale = height / (double)current.Height;
                current = Track(current.Resize(horizontalScale, kernel: Enums.Kernel.Lanczos3, vscale: verticalScale), generated);
            }

            if (descreenLevel > 0)
            {
                var hasAlpha = current.HasAlpha();
                VipsImage colour = current;
                VipsImage? alpha = null;
                if (hasAlpha && current.Bands > 1)
                {
                    colour = Track(current.ExtractBand(0, n: current.Bands - 1), generated);
                    alpha = Track(current.ExtractBand(current.Bands - 1), generated);
                }

                var gray = Track(colour.Colourspace(Enums.Interpretation.Bw), generated);
                var smoothed = Track(gray.Gaussblur(DescreenSigma(descreenLevel), minAmpl: 0.01), generated);
                current = alpha is null
                    ? smoothed
                    : Track(smoothed.Bandjoin(new[] { alpha }), generated);
            }

            if (flattenForJpeg && autoGrayscale && IsClearlyMonochrome(current))
            {
                if (current.Bands != 1 || current.Interpretation is not (Enums.Interpretation.Bw or Enums.Interpretation.Grey16))
                    current = Track(current.Colourspace(Enums.Interpretation.Bw), generated);
            }

            current.SetProgress(new Progress<int>(_ => { }), cancellationToken);
            consume(current);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            for (var index = generated.Count - 1; index >= 0; index--)
                generated[index].Dispose();
        }
    }

    private async Task EncodeJpegFromVipsAsync(
        string input,
        string output,
        int targetWidth,
        int targetHeight,
        int quality,
        bool autoGrayscale,
        int descreenLevel,
        CancellationToken cancellationToken)
    {
        var isFirstCall = BeginJpegliFirstCall();
        try
        {
            await _jpegEncoder.EncodePnmAsync(
                (stream, token) => Task.Run(
                    () => PreparePnmStreamWithVips(
                        input,
                        stream,
                        targetWidth,
                        targetHeight,
                        autoGrayscale,
                        descreenLevel,
                        token),
                    token),
                output,
                quality,
                cancellationToken).ConfigureAwait(false);
            CompleteJpegliFirstCall(isFirstCall, true);
        }
        catch
        {
            CompleteJpegliFirstCall(isFirstCall, false);
            throw;
        }
    }

    private static void PreparePnmStreamWithVips(
        string input,
        Stream output,
        int targetWidth,
        int targetHeight,
        bool autoGrayscale,
        int descreenLevel,
        CancellationToken cancellationToken)
    {
        WithPreparedImage(
            input,
            targetWidth,
            targetHeight,
            true,
            autoGrayscale,
            descreenLevel,
            cancellationToken,
            current =>
            {
                var normalized = new List<VipsImage>();
                try
                {
                    var image = current;
                    if (current.Bands != 1 && current.Bands != 3)
                    {
                        image = Track(current.Colourspace(Enums.Interpretation.Srgb), normalized);
                    }
                    if (image.Format != Enums.BandFormat.Uchar)
                        image = Track(image.Cast(Enums.BandFormat.Uchar, shift: true), normalized);

                    WritePnmStripes(image, output, cancellationToken);
                }
                finally
                {
                    for (var index = normalized.Count - 1; index >= 0; index--)
                        normalized[index].Dispose();
                }
            });
    }

    private static void WritePnmStripes(VipsImage image, Stream output, CancellationToken cancellationToken)
    {
        if (image.Bands is not (1 or 3) || image.Format != Enums.BandFormat.Uchar)
            throw new InvalidOperationException("Jpegli 流式输入必须是 8 位灰度或 RGB 图片。");

        var header = Encoding.ASCII.GetBytes($"{(image.Bands == 1 ? "P5" : "P6")}\n{image.Width} {image.Height}\n255\n");
        output.Write(header, 0, header.Length);
        using var region = Region.New(image);
        const int stripeRows = 32;
        for (var top = 0; top < image.Height; top += stripeRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var height = Math.Min(stripeRows, image.Height - top);
            var pixels = region.Fetch(0, top, image.Width, height);
            output.Write(pixels, 0, pixels.Length);
        }
    }

    private static VipsImage Track(VipsImage image, ICollection<VipsImage> generated)
    {
        generated.Add(image);
        return image;
    }

    private static double DescreenSigma(int level)
        => Math.Clamp(level, 1, 3) switch
        {
            1 => 0.55,
            2 => 0.95,
            _ => 1.45
        };

    private static bool IsClearlyMonochrome(VipsImage image)
    {
        var colourBands = image.HasAlpha() ? image.Bands - 1 : image.Bands;
        if (colourBands <= 1) return true;

        try
        {
            using var colour = colourBands == image.Bands
                ? image.Copy()
                : image.ExtractBand(0, n: colourBands);
            using var srgb = colour.Interpretation is Enums.Interpretation.Srgb or Enums.Interpretation.Rgb or Enums.Interpretation.Rgb16
                ? colour.Copy()
                : colour.Colourspace(Enums.Interpretation.Srgb);
            if (srgb.Bands < 3) return true;
            using var red = srgb.ExtractBand(0);
            using var green = srgb.ExtractBand(1);
            using var blue = srgb.ExtractBand(2);
            using var redGreen = red.Subtract(green).Abs();
            using var redBlue = red.Subtract(blue).Abs();
            using var greenBlue = green.Subtract(blue).Abs();
            using var firstMaximum = redGreen.Maxpair(redBlue);
            using var channelDelta = firstMaximum.Maxpair(greenBlue);
            var tolerance = srgb.Format == Enums.BandFormat.Ushort ? 384d : 1.5d;
            return channelDelta.Max() <= tolerance;
        }
        catch
        {
            // An uncertain colour space must stay in colour; false negatives only
            // cost a little space, while false positives can destroy real colour.
            return false;
        }
    }

    private async Task EncodeJpegliAsync(string input, string output, int quality, CancellationToken cancellationToken)
    {
        var isFirstCall = BeginJpegliFirstCall();
        try
        {
            await _jpegEncoder.EncodeAsync(input, output, quality, cancellationToken).ConfigureAwait(false);
            CompleteJpegliFirstCall(isFirstCall, true);
        }
        catch
        {
            CompleteJpegliFirstCall(isFirstCall, false);
            throw;
        }
    }

    private bool BeginJpegliFirstCall()
    {
        var isFirstCall = Interlocked.CompareExchange(ref _jpegliFirstCallStarted, 1, 0) == 0;
        if (isFirstCall) StartupTelemetry.Mark("image.jpegli.firstCall.start");
        return isFirstCall;
    }

    private static void CompleteJpegliFirstCall(bool isFirstCall, bool succeeded)
    {
        if (!isFirstCall) return;
        StartupTelemetry.Mark(succeeded ? "image.jpegli.firstCall.complete" : "image.jpegli.firstCall.failed");
        StartupTelemetry.FlushInBackground("first-jpegli-call");
    }

    private async Task DecodeFallbackAsync(string input, string output, CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-i", input, "-frames:v", "1", "-map_metadata", "0", output
        };
        await RunProcessAsync(_ffmpegPath, arguments, output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunProcessAsync(
        string executable,
        IEnumerable<string> arguments,
        string output,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动图片处理引擎。");
        var errorTask = process.StandardError.ReadToEndAsync();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            TryDelete(output);
            throw;
        }

        var error = await errorTask.ConfigureAwait(false);
        var standardOutput = await outputTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(output))
        {
            TryDelete(output);
            throw new InvalidOperationException(LastLine(string.IsNullOrWhiteSpace(error) ? standardOutput : error));
        }
    }

    private string TemporaryPath(string extension)
        => Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N") + extension);

    private static string NormalizeExtension(string extension, string input)
    {
        var value = string.IsNullOrWhiteSpace(extension) ? Path.GetExtension(input) : extension;
        if (!value.StartsWith('.')) value = "." + value;
        value = value.ToLowerInvariant();
        if (value == ".jpeg") return ".jpg";
        return value is ".png" or ".jpg" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff"
            ? value
            : ".png";
    }

    private static double WhiteLevel(Enums.BandFormat format)
        => format switch
        {
            Enums.BandFormat.Ushort => ushort.MaxValue,
            Enums.BandFormat.Short => short.MaxValue,
            Enums.BandFormat.Uint => uint.MaxValue,
            Enums.BandFormat.Int => int.MaxValue,
            _ => byte.MaxValue
        };

    private static string LastLine(string value)
    {
        var lines = value.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "图片处理失败。" : lines[^1];
    }

    private InvalidOperationException MissingCompatibilityDecoder(string input, Exception firstError)
        => new(
            $"libvips 无法解码“{Path.GetFileName(input)}”，并且尚未安装可选 FFmpeg 兼容组件。" +
            "请安装与本版本配套的兼容包后重试。libvips：" + firstError.Message,
            firstError);

    private static bool IsVipsFailure(Exception exception)
        => exception is VipsException || exception.InnerException is not null && IsVipsFailure(exception.InnerException);

    private static void DisableOperationCache()
    {
        if (ModuleInitializer.VipsInitialized)
            NetVips.Cache.Max = 0;
    }

    private void EnsureVipsInitialized()
    {
        if (_vipsReady) return;
        lock (_vipsInitializationGate)
        {
            if (_vipsReady) return;
            StartupTelemetry.Mark("image.libvips.initialize.start");
            try
            {
                if (!ModuleInitializer.VipsInitialized)
                    throw new InvalidOperationException(ModuleInitializer.Exception?.Message ?? "未知初始化错误。");
                NetVips.Cache.Max = 0;
                _vipsReady = true;
                _vipsInitializationError = null;
                StartupTelemetry.Mark("image.libvips.initialize.complete");
                StartupTelemetry.FlushInBackground("first-libvips-initialization");
            }
            catch (Exception exception)
            {
                _vipsInitializationError = exception;
                StartupTelemetry.Mark("image.libvips.initialize.failed");
                StartupTelemetry.FlushInBackground("libvips-initialization-failed");
                throw new InvalidOperationException("图片处理引擎 libvips 初始化失败：" + (_vipsInitializationError.Message), exception);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
