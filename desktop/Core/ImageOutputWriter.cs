namespace AichanToolbox.Core;

internal sealed record ImageOutputResult(string Path, long Size, bool Replaced, string? Warning = null);

internal sealed class ImageOutputWriter
{
    private readonly object _gate = new();
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _recycleFile;

    public ImageOutputWriter(Action<string>? recycleFile = null)
        => _recycleFile = recycleFile ?? ShellRecycleBin.DeleteFile;

    public ImageOutputResult Write(FileJob job, ExecutionResult result, WorkflowNode outputNode)
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
                return new ImageOutputResult(job.SourcePath, new FileInfo(job.SourcePath).Length, false);

            lock (_gate)
            {
                var desired = Path.Combine(sourceDirectory, baseName + resultExtension);
                if (!desired.Equals(job.SourcePath, StringComparison.OrdinalIgnoreCase) && File.Exists(desired))
                    desired = Reserve(sourceDirectory, baseName, resultExtension);

                // Save a complete, usable result before asking Windows to recycle
                // the original. A failed recycle must not discard this result.
                var saved = Reserve(sourceDirectory, baseName + "_processed", resultExtension);
                CopyResult(result.FinalPath, saved);
                var size = new FileInfo(saved).Length;
                string? warning = null;
                try
                {
                    DesktopBridge.WaitForExclusiveAccess(job.SourcePath);
                    _recycleFile(job.SourcePath);
                    if (File.Exists(job.SourcePath))
                        throw new IOException("回收站操作结束后原文件仍然存在。");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    if (File.Exists(job.SourcePath))
                        return new ImageOutputResult(saved, size, false,
                            $"原图未替换：{exception.Message} 处理结果已另存到：{saved}");
                    warning = $"回收站报告异常：{exception.Message}";
                }

                try
                {
                    File.Move(saved, desired, false);
                    return new ImageOutputResult(desired, size, true, warning);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The original is already in the Recycle Bin. Keep the saved
                    // result if its final name became occupied or inaccessible.
                    return new ImageOutputResult(saved, size, true,
                        $"原文件已回收，但无法使用原文件名：{exception.Message} 处理结果已保存在：{saved}");
                }
            }
        }

        var changedFormat = !sourceExtension.Equals(resultExtension, StringComparison.OrdinalIgnoreCase)
            && !((sourceExtension is ".jpg" or ".jpeg") && (resultExtension is ".jpg" or ".jpeg"));
        var output = Reserve(directory, changedFormat ? baseName : baseName + "_processed", resultExtension);
        CopyResult(result.FinalPath, output);
        return new ImageOutputResult(output, new FileInfo(output).Length, false);
    }

    public string Reserve(string directory, string baseName, string extension)
    {
        lock (_gate)
        {
            var candidate = Path.Combine(directory, baseName + extension);
            var number = 1;
            while (File.Exists(candidate) || _reserved.Contains(candidate))
                candidate = Path.Combine(directory, baseName + "_" + number++ + extension);
            _reserved.Add(candidate);
            return candidate;
        }
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
