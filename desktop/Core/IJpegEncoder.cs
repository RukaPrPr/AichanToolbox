using System.Diagnostics;

namespace AichanToolbox.Core;

internal interface IJpegEncoder
{
    string Name { get; }
    bool IsAvailable { get; }
    Task EncodeAsync(string inputPath, string outputPath, int quality, CancellationToken cancellationToken);
    Task EncodePnmAsync(
        Func<Stream, CancellationToken, Task> writeInput,
        string outputPath,
        int quality,
        CancellationToken cancellationToken);
}

internal sealed class JpegliProcessEncoder : IJpegEncoder
{
    private readonly string _executablePath;

    public JpegliProcessEncoder(string executablePath) => _executablePath = executablePath;

    public string Name => "Jpegli (兼容进程模式)";
    public bool IsAvailable => File.Exists(_executablePath);

    public async Task EncodeAsync(string inputPath, string outputPath, int quality, CancellationToken cancellationToken)
        => await EncodeProcessAsync(inputPath, null, outputPath, quality, cancellationToken).ConfigureAwait(false);

    public async Task EncodePnmAsync(
        Func<Stream, CancellationToken, Task> writeInput,
        string outputPath,
        int quality,
        CancellationToken cancellationToken)
        => await EncodeProcessAsync("-", writeInput, outputPath, quality, cancellationToken).ConfigureAwait(false);

    private async Task EncodeProcessAsync(
        string inputArgument,
        Func<Stream, CancellationToken, Task>? writeInput,
        string outputPath,
        int quality,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable) throw new InvalidOperationException("高画质编码器 cjpegli.exe 缺失。");
        var start = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = writeInput is not null,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in new[]
        {
            inputArgument,
            outputPath,
            "--quality=" + Math.Clamp(quality, 1, 100),
            "--chroma_subsampling=444",
            "--progressive_level=2"
        }) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Jpegli 编码器。");
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        var errorTask = process.StandardError.ReadToEndAsync();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        Task? inputTask = null;
        if (writeInput is not null)
        {
            inputTask = Task.Run(async () =>
            {
                try
                {
                    await writeInput(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    try { process.StandardInput.Close(); } catch { }
                }
            }, CancellationToken.None);
        }

        try
        {
            if (inputTask is not null)
                await inputTask.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            TryDelete(outputPath);
            throw;
        }

        var error = await errorTask.ConfigureAwait(false);
        var standardOutput = await outputTask.ConfigureAwait(false);
        if (process.ExitCode == 0 && File.Exists(outputPath)) return;
        TryDelete(outputPath);
        throw new InvalidOperationException(LastLine(string.IsNullOrWhiteSpace(error) ? standardOutput : error));
    }

    private static string LastLine(string value)
    {
        var lines = value.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "Jpegli 编码失败。" : lines[^1];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
