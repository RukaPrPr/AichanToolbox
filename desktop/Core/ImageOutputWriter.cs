namespace AichanToolbox.Core;

internal sealed record ImageOutputResult(string Path, long Size, bool Replaced, string? Warning = null);

internal sealed record PendingImageReplacement(
    string SourcePath,
    string DesiredPath,
    string SavedPath,
    long Size,
    FileAttributes FinalAttributes);

internal sealed record PreparedImageOutput(
    ImageOutputResult? Completed,
    PendingImageReplacement? Replacement);

internal sealed class ImageOutputWriter
{
    private readonly object _gate = new();
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<RecycleFileResult>> _recycleFiles;

    public ImageOutputWriter(
        Action<string>? recycleFile = null,
        Func<IReadOnlyList<string>, IReadOnlyList<RecycleFileResult>>? recycleFiles = null)
    {
        if (recycleFiles is not null)
        {
            _recycleFiles = recycleFiles;
            return;
        }

        if (recycleFile is not null)
        {
            _recycleFiles = paths =>
            {
                foreach (var path in paths) recycleFile(path);
                return paths.Select(path => new RecycleFileResult(path, !File.Exists(path), null)).ToList();
            };
            return;
        }

        _recycleFiles = ShellRecycleBin.DeleteFiles;
    }

    public ImageOutputResult Write(FileJob job, ExecutionResult result, WorkflowNode outputNode)
    {
        var prepared = Prepare(job, result, outputNode);
        if (prepared.Completed is not null) return prepared.Completed;
        return CommitReplacements([prepared.Replacement!]).Single();
    }

    public PreparedImageOutput Prepare(FileJob job, ExecutionResult result, WorkflowNode outputNode)
    {
        var sourceExtension = Path.GetExtension(job.SourcePath).ToLowerInvariant();
        var resultExtension = Path.GetExtension(result.FinalPath).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(resultExtension)) resultExtension = ".jpg";
        var sourceDirectory = Path.GetDirectoryName(job.SourcePath) ?? Environment.CurrentDirectory;
        var directory = outputNode.Data.SameFolder ? sourceDirectory : outputNode.Data.OutputDirectory;
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("保存输出节点没有设置输出目录。");
        Directory.CreateDirectory(directory);
        var baseName = Path.GetFileNameWithoutExtension(job.SourcePath);

        if (outputNode.Data.ReplaceOriginal)
        {
            if (!result.Transformed && result.FinalPath.Equals(job.SourcePath, StringComparison.OrdinalIgnoreCase))
                return new PreparedImageOutput(
                    new ImageOutputResult(job.SourcePath, new FileInfo(job.SourcePath).Length, false),
                    null);

            string desired;
            string saved;
            lock (_gate)
            {
                desired = Path.Combine(sourceDirectory, baseName + resultExtension);
                if (!desired.Equals(job.SourcePath, StringComparison.OrdinalIgnoreCase)
                    && (File.Exists(desired) || _reserved.Contains(desired)))
                    desired = ReserveCore(sourceDirectory, baseName, resultExtension);
                else
                    _reserved.Add(desired);
                saved = ReserveCore(sourceDirectory, baseName + "_processed", resultExtension);
            }

            // Every result is fully copied before any source file enters the
            // batched Recycle Bin transaction. A failed recycle keeps this copy.
            CopyResult(result.FinalPath, saved);
            var finalAttributes = HideStagedOutput(saved);
            return new PreparedImageOutput(
                null,
                new PendingImageReplacement(
                    job.SourcePath,
                    desired,
                    saved,
                    new FileInfo(saved).Length,
                    finalAttributes));
        }

        var changedFormat = !sourceExtension.Equals(resultExtension, StringComparison.OrdinalIgnoreCase)
            && !((sourceExtension is ".jpg" or ".jpeg") && (resultExtension is ".jpg" or ".jpeg"));
        var output = Reserve(directory, changedFormat ? baseName : baseName + "_processed", resultExtension);
        CopyResult(result.FinalPath, output);
        return new PreparedImageOutput(
            new ImageOutputResult(output, new FileInfo(output).Length, false),
            null);
    }

    public IReadOnlyList<ImageOutputResult> CommitReplacements(
        IReadOnlyList<PendingImageReplacement> replacements)
    {
        if (replacements.Count == 0) return [];

        var readinessErrors = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
        var readyPaths = new List<string>(replacements.Count);
        foreach (var path in replacements.Select(value => value.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                readyPaths.Add(path);
                continue;
            }

            try
            {
                DesktopBridge.WaitForExclusiveAccess(path);
                readyPaths.Add(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                readinessErrors[path] = exception;
            }
        }

        IReadOnlyList<RecycleFileResult> recycleResults;
        try
        {
            recycleResults = _recycleFiles(readyPaths);
        }
        catch (Exception exception)
        {
            recycleResults = readyPaths
                .Select(path => new RecycleFileResult(path, !File.Exists(path), exception))
                .ToList();
        }

        var recycleByPath = recycleResults.ToDictionary(value => value.Path, StringComparer.OrdinalIgnoreCase);
        return replacements.Select(replacement =>
        {
            readinessErrors.TryGetValue(replacement.SourcePath, out var readinessError);
            recycleByPath.TryGetValue(replacement.SourcePath, out var recycle);
            var error = readinessError ?? recycle?.Error;
            if (File.Exists(replacement.SourcePath))
            {
                error ??= new IOException("Windows 回收站操作结束后原文件仍然存在。");
                var attributeWarning = RestoreOutputAttributes(replacement.SavedPath, replacement.FinalAttributes);
                return new ImageOutputResult(
                    replacement.SavedPath,
                    replacement.Size,
                    false,
                    CombineWarnings(
                        $"原图未替换：{error.Message} 处理结果已另存到：{replacement.SavedPath}",
                        attributeWarning));
            }

            var warning = error is null ? null : $"回收站报告异常：{error.Message}";
            try
            {
                File.Move(replacement.SavedPath, replacement.DesiredPath, false);
                var attributeWarning = RestoreOutputAttributes(replacement.DesiredPath, replacement.FinalAttributes);
                return new ImageOutputResult(
                    replacement.DesiredPath,
                    replacement.Size,
                    true,
                    CombineWarnings(warning, attributeWarning));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The source is already in the Recycle Bin. Keep the complete
                // staged output if its final name became occupied or inaccessible.
                var attributeWarning = RestoreOutputAttributes(replacement.SavedPath, replacement.FinalAttributes);
                return new ImageOutputResult(
                    replacement.SavedPath,
                    replacement.Size,
                    true,
                    CombineWarnings(
                        $"原文件已回收，但无法使用原文件名：{exception.Message} 处理结果已保存在：{replacement.SavedPath}",
                        attributeWarning));
            }
        }).ToList();
    }

    public void Discard(PendingImageReplacement replacement)
    {
        try { if (File.Exists(replacement.SavedPath)) File.Delete(replacement.SavedPath); }
        catch { }
    }

    public string Reserve(string directory, string baseName, string extension)
    {
        lock (_gate) return ReserveCore(directory, baseName, extension);
    }

    private string ReserveCore(string directory, string baseName, string extension)
    {
        var candidate = Path.Combine(directory, baseName + extension);
        var number = 1;
        while (File.Exists(candidate) || _reserved.Contains(candidate))
            candidate = Path.Combine(directory, baseName + "_" + number++ + extension);
        _reserved.Add(candidate);
        return candidate;
    }

    private static FileAttributes HideStagedOutput(string path)
    {
        var attributes = File.GetAttributes(path);
        try { File.SetAttributes(path, attributes | FileAttributes.Hidden | FileAttributes.Temporary); }
        catch { }
        return attributes;
    }

    private static string? RestoreOutputAttributes(string path, FileAttributes attributes)
    {
        try
        {
            File.SetAttributes(path, attributes);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"无法恢复输出文件属性：{exception.Message}";
        }
    }

    private static string? CombineWarnings(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return first + " " + second;
    }

    private static void CopyResult(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try { input.CopyTo(output); }
        catch
        {
            output.Dispose();
            try { File.Delete(destination); } catch { }
            throw;
        }
    }
}
