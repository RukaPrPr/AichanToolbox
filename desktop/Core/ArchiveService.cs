using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpZipArchive = SharpCompress.Archives.Zip.ZipArchive;

namespace AichanToolbox.Core;

internal sealed class ArchiveService
{
    private const int MaximumEntries = 100_000;
    private const long MaximumExpandedBytes = 100L * 1024 * 1024 * 1024;

    static ArchiveService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public bool IsPrepared(ArchiveJob job)
        => File.Exists(job.SourcePath)
           && job.PreparedFingerprint == Fingerprint(job.SourcePath)
           && Directory.Exists(job.OutputDirectory)
           && job.Entries.Count > 0
           && job.Entries.Where(entry => entry.IsImage).All(entry => File.Exists(entry.ExtractedPath));

    public async Task ExtractAsync(
        ArchiveJob job,
        string encodingName,
        string? password,
        IProgress<(int Completed, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(job.SourcePath)) throw new FileNotFoundException("ZIP 文件不存在。", job.SourcePath);
        if (IsPrepared(job))
        {
            job.Status = "预处理完成 · 已复用";
            job.Progress = 100;
            progress?.Report((job.EntryCount, job.EntryCount));
            return;
        }

        var sourceDirectory = Path.GetDirectoryName(job.SourcePath) ?? Environment.CurrentDirectory;
        var baseName = Path.GetFileNameWithoutExtension(job.SourcePath);
        var destination = AvailableDirectory(sourceDirectory, baseName);
        var staging = Path.Combine(sourceDirectory, $".{baseName}.aichan-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            var options = ReaderOptions.ForFilePath;
            if (!string.IsNullOrEmpty(password)) options = options.WithPassword(password);
            var encoding = ResolveEncoding(encodingName);
            if (encoding is not null)
                options = options.WithArchiveEncoding(new ArchiveEncoding { Default = encoding });

            using var archive = SharpZipArchive.OpenArchive(job.SourcePath, options);
            var entries = archive.Entries.ToList();
            if (entries.Count > MaximumEntries)
                throw new InvalidDataException($"ZIP 条目数量超过安全限制（{MaximumEntries:N0}）。");

            var expandedBytes = entries.Where(entry => !entry.IsDirectory).Sum(entry => checked((long)entry.Size));
            if (expandedBytes > MaximumExpandedBytes)
                throw new InvalidDataException("ZIP 解压后的总体积超过 100 GB 安全限制。");

            job.EntryCount = entries.Count(entry => !entry.IsDirectory);
            job.Progress = 0;
            job.Status = "正在解压";
            var extracted = new List<ArchiveEntryRecord>();
            var completed = 0;
            var stagingRoot = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryPath = NormalizeEntryPath(entry.Key ?? "");
                if (entryPath.Length == 0) continue;
                var target = Path.GetFullPath(Path.Combine(staging, entryPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"ZIP 包含越界路径：{entry.Key}");

                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var source = entry.OpenEntryStream())
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                    await source.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);

                completed++;
                job.Progress = job.EntryCount == 0 ? 100 : (int)Math.Round(completed * 100d / job.EntryCount);
                progress?.Report((completed, job.EntryCount));
                extracted.Add(new ArchiveEntryRecord
                {
                    EntryPath = entryPath,
                    ExtractedPath = target,
                    IsImage = IsSupportedImage(target),
                    Size = new FileInfo(target).Length
                });
            }

            Directory.Move(staging, destination);
            foreach (var entry in extracted)
                entry.ExtractedPath = Path.Combine(destination, Path.GetRelativePath(staging, entry.ExtractedPath));
            job.OutputDirectory = destination;
            job.Entries = extracted;
            job.OwnsOutputDirectory = true;
            job.ImageCount = extracted.Count(entry => entry.IsImage);
            job.PreparedFingerprint = Fingerprint(job.SourcePath);
            job.Progress = 100;
            job.Status = $"预处理完成 · {job.ImageCount} 张图片";
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            throw;
        }
    }

    public async Task<string> PackStoreAsync(
        ArchiveJob archiveJob,
        IReadOnlyDictionary<string, string> replacements,
        bool preserveNonImages,
        string destination,
        IProgress<(int Completed, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        var entries = archiveJob.Entries
            .Where(entry => entry.IsImage || preserveNonImages)
            .ToList();
        if (entries.Count == 0) throw new InvalidOperationException("没有可以写入 ZIP 的文件。");

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, true);
        using (var zip = new System.IO.Compression.ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
        {
            var completed = 0;
            foreach (var sourceEntry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryName = sourceEntry.EntryPath;
                var sourcePath = sourceEntry.ExtractedPath;
                if (sourceEntry.IsImage && replacements.TryGetValue(sourceEntry.EntryPath, out var replacement))
                {
                    sourcePath = replacement;
                    entryName = ReplaceExtension(sourceEntry.EntryPath, Path.GetExtension(replacement));
                }
                if (!File.Exists(sourcePath)) throw new FileNotFoundException("打包源文件不存在。", sourcePath);
                if (!usedNames.Add(entryName)) throw new InvalidOperationException($"ZIP 内产生了重复路径：{entryName}");

                var targetEntry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
                await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                await using var target = targetEntry.Open();
                await source.CopyToAsync(target, 128 * 1024, cancellationToken).ConfigureAwait(false);
                completed++;
                progress?.Report((completed, entries.Count));
            }
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return destination;
    }

    public static async Task VerifyAsync(string path, int expectedEntries, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        using var zip = new System.IO.Compression.ZipArchive(input, ZipArchiveMode.Read, true, Encoding.UTF8);
        if (zip.Entries.Count != expectedEntries)
            throw new InvalidDataException($"ZIP 校验失败：应有 {expectedEntries} 个文件，实际为 {zip.Entries.Count} 个。");
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = entry.Open();
            await stream.CopyToAsync(Stream.Null, 128 * 1024, cancellationToken).ConfigureAwait(false);
        }
    }

    public static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    public void DeleteExtractionDirectory(ArchiveJob job)
    {
        if (!job.OwnsOutputDirectory || string.IsNullOrWhiteSpace(job.OutputDirectory))
            throw new InvalidOperationException("没有可安全删除的解压目录。");

        var sourceDirectory = Path.GetFullPath(Path.GetDirectoryName(job.SourcePath) ?? Environment.CurrentDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var outputDirectory = Path.GetFullPath(job.OutputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var outputParent = Path.GetDirectoryName(outputDirectory)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var baseName = Path.GetFileNameWithoutExtension(job.SourcePath);
        var outputName = Path.GetFileName(outputDirectory);
        if (!string.Equals(sourceDirectory, outputParent, StringComparison.OrdinalIgnoreCase)
            || !IsManagedExtractionName(outputName, baseName)
            || string.Equals(sourceDirectory, outputDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝删除未由 ZIP 解压节点创建的目录。");

        if (Directory.Exists(outputDirectory)) DeleteTreeWithoutFollowingLinks(outputDirectory);
        job.OutputDirectory = "";
        job.PreparedFingerprint = "";
        job.Entries.Clear();
        job.OwnsOutputDirectory = false;
    }

    private static string AvailableDirectory(string parent, string baseName)
    {
        var candidate = Path.Combine(parent, baseName);
        var number = 2;
        while (Directory.Exists(candidate) || File.Exists(candidate))
            candidate = Path.Combine(parent, $"{baseName} ({number++})");
        return candidate;
    }

    private static Encoding? ResolveEncoding(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "utf8" => new UTF8Encoding(false),
            "gb18030" => Encoding.GetEncoding("GB18030"),
            "cp932" => Encoding.GetEncoding(932),
            _ => null
        };

    private static bool IsManagedExtractionName(string value, string baseName)
    {
        if (value.Equals(baseName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!value.StartsWith(baseName + " (", StringComparison.OrdinalIgnoreCase) || !value.EndsWith(')')) return false;
        var numberText = value[(baseName.Length + 2)..^1];
        return int.TryParse(numberText, out var number) && number >= 2;
    }

    private static void DeleteTreeWithoutFollowingLinks(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            File.Delete(file);
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(child, false);
            else DeleteTreeWithoutFollowingLinks(child);
        }
        Directory.Delete(directory, false);
    }

    private static string NormalizeEntryPath(string value)
    {
        value = value.Replace('\\', '/').TrimStart('/');
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        if (value.Contains('\0')) throw new InvalidDataException("ZIP 条目名称包含无效字符。");
        return value;
    }

    private static string ReplaceExtension(string entryPath, string extension)
    {
        var directory = Path.GetDirectoryName(entryPath.Replace('/', Path.DirectorySeparatorChar));
        var name = Path.GetFileNameWithoutExtension(entryPath) + extension.ToLowerInvariant();
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name).Replace('\\', '/');
    }

    internal static bool IsSupportedImage(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".avif" or ".heic" or ".heif";
}
