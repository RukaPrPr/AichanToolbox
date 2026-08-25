namespace AichanToolbox.Core;

internal sealed class WorkflowRunner
{
    private readonly ImageEngine _engine;
    private readonly TargetSizeOptimizer _targetSizeOptimizer;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentQueue<int>> _targetSizeHistory = new(StringComparer.Ordinal);

    public WorkflowRunner(ImageEngine engine)
    {
        _engine = engine;
        _targetSizeOptimizer = new TargetSizeOptimizer(engine);
    }

    public async Task<ExecutionResult> ExecuteAsync(
        FileJob job,
        WorkflowDocument workflow,
        CancellationToken cancellationToken)
    {
        var nodes = workflow.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var import = workflow.Nodes.FirstOrDefault(node => node.Type == "Import")
            ?? throw new InvalidOperationException("工作流缺少导入节点。");
        var connection = FindConnection(workflow, import.Id, "out")
            ?? throw new InvalidOperationException("导入节点没有连接到后续节点。");

        var state = new ExecutionState
        {
            SourcePath = job.SourcePath,
            CurrentPath = job.SourcePath,
            TargetExtension = NormalizeExtension(Path.GetExtension(job.SourcePath)),
            Width = job.CurrentWidth > 0 ? job.CurrentWidth : job.OriginalWidth,
            Height = job.CurrentHeight > 0 ? job.CurrentHeight : job.OriginalHeight,
            Size = job.CurrentSize > 0 ? job.CurrentSize : job.OriginalSize,
            AutoGrayscale = workflow.AutoGrayscale
        };
        if (!string.IsNullOrWhiteSpace(job.OriginNodeId)) state.RouteNodeIds.Add(job.OriginNodeId);
        if (!string.IsNullOrWhiteSpace(job.OriginConnectionId)) state.RouteConnectionIds.Add(job.OriginConnectionId);
        state.RouteNodeIds.Add(import.Id);

        while (connection is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.RouteConnectionIds.Add(connection.Id);
            if (!nodes.TryGetValue(connection.ToNodeId, out var node))
                throw new InvalidOperationException("工作流包含无效连线。");
            if (!state.VisitedNodes.Add(node.Id))
                throw new InvalidOperationException("工作流中检测到循环连接。");
            state.RouteNodeIds.Add(node.Id);

            if (node.Type == "Output")
            {
                await EnsureMaterializedAsync(state, cancellationToken).ConfigureAwait(false);
                return new ExecutionResult
                {
                    FinalPath = state.CurrentPath,
                    OutputNodeId = node.Id,
                    Width = state.Width,
                    Height = state.Height,
                    Size = state.Size,
                    Transformed = state.Transformed,
                    RouteNodeIds = state.RouteNodeIds.ToList(),
                    RouteConnectionIds = state.RouteConnectionIds.ToList(),
                    TemporaryFiles = state.TemporaryFiles.ToList()
                };
            }

            string nextPort;
            switch (node.Type)
            {
                case "FormatFilter":
                    nextPort = RouteFormat(state.TargetExtension);
                    break;
                case "SizeFilter":
                    await EnsureMaterializedAsync(state, cancellationToken).ConfigureAwait(false);
                    nextPort = SizeMatches(state, node) ? "match" : "else";
                    break;
                case "ResolutionFilter":
                    nextPort = ResolutionMatches(state, node) ? "match" : "else";
                    break;
                case "ConvertJpg":
                    ConvertToJpeg(state);
                    nextPort = "out";
                    break;
                case "Resize":
                    Resize(state, node.Data.ScalePercent);
                    nextPort = "out";
                    break;
                case "Descreen":
                    ApplyDescreen(state, node.Data.DescreenLevel);
                    nextPort = "out";
                    break;
                case "Quality":
                    ApplyJpegQuality(state, node.Data.QualityPercent);
                    nextPort = "out";
                    break;
                case "TargetSize":
                    await ApplyTargetSizeAsync(state, node, cancellationToken).ConfigureAwait(false);
                    nextPort = "out";
                    break;
                default:
                    throw new InvalidOperationException($"不支持的节点：{node.Title}");
            }

            connection = FindConnection(workflow, node.Id, nextPort);
            if (connection is null)
                throw new InvalidOperationException($"节点“{node.Title}”的 {nextPort} 出口没有连接。");
        }

        throw new InvalidOperationException("工作流没有到达保存输出节点。");
    }

    private async Task EnsureMaterializedAsync(ExecutionState state, CancellationToken cancellationToken)
    {
        if (!state.Transformed)
        {
            state.CurrentPath = state.SourcePath;
            state.Size = new FileInfo(state.SourcePath).Length;
            return;
        }

        if (state.MaterializedVersion == state.RenderVersion && File.Exists(state.CurrentPath))
        {
            state.Size = new FileInfo(state.CurrentPath).Length;
            return;
        }

        var nextPath = await _engine.RenderAsync(
            state.SourcePath,
            state.TargetExtension,
            state.Width,
            state.Height,
            state.JpegQuality,
            state.AutoGrayscale,
            state.DescreenLevel,
            cancellationToken).ConfigureAwait(false);

        if (!state.CurrentPath.Equals(state.SourcePath, StringComparison.OrdinalIgnoreCase) && File.Exists(state.CurrentPath))
            state.TemporaryFiles.Add(state.CurrentPath);
        state.CurrentPath = nextPath;
        state.Size = new FileInfo(nextPath).Length;
        state.MaterializedVersion = state.RenderVersion;
    }

    private static void ConvertToJpeg(ExecutionState state)
    {
        if (state.TargetExtension is ".jpg" or ".jpeg" && state.JpegQuality == 100)
            return;

        state.TargetExtension = ".jpg";
        state.JpegQuality = 100;
        MarkDirty(state);
    }

    private static void Resize(ExecutionState state, int scalePercent)
    {
        var scale = Math.Clamp(scalePercent, 20, 100);
        if (scale == 100) return;

        state.Width = Math.Max(2, (int)Math.Truncate(state.Width * (scale / 100d)));
        state.Height = Math.Max(2, (int)Math.Truncate(state.Height * (scale / 100d)));
        MarkDirty(state);
    }

    private static void ApplyJpegQuality(ExecutionState state, int qualityPercent)
    {
        state.TargetExtension = ".jpg";
        state.JpegQuality = Math.Clamp(qualityPercent, 20, 100);
        MarkDirty(state);
    }

    private static void ApplyDescreen(ExecutionState state, int level)
    {
        var normalized = Math.Clamp(level, 1, 3);
        if (state.DescreenLevel == normalized) return;
        state.DescreenLevel = normalized;
        MarkDirty(state);
    }

    private async Task ApplyTargetSizeAsync(
        ExecutionState state,
        WorkflowNode node,
        CancellationToken cancellationToken)
    {
        var settings = node.Data;
        var targetMb = Math.Clamp(settings.TargetSizeMb, 0.01, 1024);
        var targetBytes = Math.Max(1L, (long)Math.Floor(targetMb * 1024d * 1024d));
        await EnsureMaterializedAsync(state, cancellationToken).ConfigureAwait(false);
        if (state.TargetExtension is not (".jpg" or ".jpeg"))
        {
            state.TargetExtension = ".jpg";
            state.JpegQuality = 100;
            MarkDirty(state);
            await EnsureMaterializedAsync(state, cancellationToken).ConfigureAwait(false);
        }
        if (state.Size <= targetBytes) return;

        var historyKey = TargetSizeHistoryKey(node.Id, state, settings, targetBytes);
        TargetSizeResult result;
        try
        {
            result = await _targetSizeOptimizer.OptimizeAsync(
                state.SourcePath,
                state.Width,
                state.Height,
                targetBytes,
                settings.TargetStartQuality,
                settings.TargetQualitySpan,
                settings.TargetMinimumQuality,
                5,
                TargetSizeHint(historyKey),
                state.AutoGrayscale,
                state.DescreenLevel,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!state.CurrentPath.Equals(state.SourcePath, StringComparison.OrdinalIgnoreCase))
                TryDelete(state.CurrentPath);
            throw;
        }
        if (!result.MetTarget)
        {
            TryDelete(result.OutputPath);
            if (!state.CurrentPath.Equals(state.SourcePath, StringComparison.OrdinalIgnoreCase))
                TryDelete(state.CurrentPath);
            throw new InvalidOperationException(
                $"最低画质 {Math.Clamp(settings.TargetMinimumQuality, 20, 99)}% 编码后仍为 {FormatSize(result.Size)}，无法达到 {targetMb:0.##} MB。请先缩小分辨率或降低画质下限。");
        }

        if (!state.CurrentPath.Equals(state.SourcePath, StringComparison.OrdinalIgnoreCase) && File.Exists(state.CurrentPath))
            state.TemporaryFiles.Add(state.CurrentPath);
        state.CurrentPath = result.OutputPath;
        state.TargetExtension = ".jpg";
        state.JpegQuality = result.Quality;
        state.Size = result.Size;
        state.Transformed = true;
        state.RenderVersion++;
        state.MaterializedVersion = state.RenderVersion;
        RecordTargetSizeQuality(historyKey, result.Quality);
    }

    private static void MarkDirty(ExecutionState state)
    {
        state.Transformed = true;
        state.RenderVersion++;
    }

    private static string RouteFormat(string extension)
        => NormalizeExtension(extension) switch
        {
            ".jpg" => "jpg",
            ".png" => "png",
            ".webp" => "webp",
            _ => "other"
        };

    private static string NormalizeExtension(string extension)
    {
        var value = string.IsNullOrWhiteSpace(extension) ? ".png" : extension.ToLowerInvariant();
        if (!value.StartsWith('.')) value = "." + value;
        return value == ".jpeg" ? ".jpg" : value;
    }

    private static WorkflowConnection? FindConnection(WorkflowDocument workflow, string nodeId, string port)
        => workflow.Connections.FirstOrDefault(connection =>
            connection.FromNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase) &&
            connection.FromPort.Equals(port, StringComparison.OrdinalIgnoreCase));

    private static bool SizeMatches(ExecutionState state, WorkflowNode node)
    {
        var target = node.Data.SizeMb * 1024d * 1024d;
        return Compare(state.Size, target, node.Data.SizeOperator);
    }

    private static bool ResolutionMatches(ExecutionState state, WorkflowNode node)
    {
        var widthMatches = node.Data.WidthEnabled && Compare(state.Width, node.Data.WidthValue, node.Data.WidthOperator);
        var heightMatches = node.Data.HeightEnabled && Compare(state.Height, node.Data.HeightValue, node.Data.HeightOperator);
        if (!node.Data.WidthEnabled && !node.Data.HeightEnabled) return true;
        if (!node.Data.WidthEnabled) return heightMatches;
        if (!node.Data.HeightEnabled) return widthMatches;
        return node.Data.ResolutionJoin.Trim().Equals("OR", StringComparison.OrdinalIgnoreCase)
            ? widthMatches || heightMatches
            : widthMatches && heightMatches;
    }

    private static bool Compare(double actual, double expected, string operation)
        => operation.Trim() switch
        {
            ">" => actual > expected,
            ">=" => actual >= expected,
            "<" => actual < expected,
            "<=" => actual <= expected,
            "=" or "==" => Math.Abs(actual - expected) < 0.0001,
            "!=" => Math.Abs(actual - expected) >= 0.0001,
            _ => actual >= expected
        };

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.00} MB"
            : $"{bytes / 1024d:0.0} KB";

    private static string TargetSizeHistoryKey(string nodeId, ExecutionState state, NodeSettings settings, long targetBytes)
    {
        var widthBucket = Math.Max(1, (int)Math.Round(state.Width / 256d));
        var heightBucket = Math.Max(1, (int)Math.Round(state.Height / 256d));
        var pixels = Math.Max(1d, state.Width * (double)state.Height);
        var bytesPerPixelBucket = (int)Math.Round(targetBytes / pixels * 100d);
        return string.Join('|', nodeId, widthBucket, heightBucket, bytesPerPixelBucket,
            Math.Clamp(settings.TargetStartQuality, 20, 99),
            Math.Clamp(settings.TargetQualitySpan, 1, 80),
            Math.Clamp(settings.TargetMinimumQuality, 20, 99),
            state.AutoGrayscale,
            state.DescreenLevel);
    }

    private int? TargetSizeHint(string key)
    {
        if (!_targetSizeHistory.TryGetValue(key, out var values) || values.IsEmpty) return null;
        var snapshot = values.ToArray();
        if (snapshot.Length == 0) return null;
        Array.Sort(snapshot);
        return snapshot[snapshot.Length / 2];
    }

    private void RecordTargetSizeQuality(string key, int quality)
    {
        var values = _targetSizeHistory.GetOrAdd(key, _ => new System.Collections.Concurrent.ConcurrentQueue<int>());
        values.Enqueue(quality);
        while (values.Count > 12) values.TryDequeue(out _);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
