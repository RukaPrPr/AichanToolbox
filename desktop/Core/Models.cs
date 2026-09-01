using System.Text.Json.Serialization;

namespace AichanToolbox.Core;

public sealed class WorkflowDocument
{
    public int Version { get; set; } = 9;
    public int Parallelism { get; set; } = 6;
    public bool AutoGrayscale { get; set; } = true;
    public bool CacheEstimates { get; set; } = true;
    public ViewportState Viewport { get; set; } = new();
    public List<WorkflowNode> Nodes { get; set; } = new();
    public List<WorkflowConnection> Connections { get; set; } = new();
}

public sealed class ViewportState
{
    public double X { get; set; } = 32;
    public double Y { get; set; } = 28;
    public double Zoom { get; set; } = 1;
}

public sealed class WorkflowNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "ConvertJpg";
    public string Title { get; set; } = "节点";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public NodeSettings Data { get; set; } = new();
}

public sealed class NodeSettings
{
    public string SizeOperator { get; set; } = ">=";
    public double SizeMb { get; set; } = 1;
    public int ScalePercent { get; set; } = 80;
    public int QualityPercent { get; set; } = 100;
    public double TargetSizeMb { get; set; } = 2;
    public int TargetStartQuality { get; set; } = 90;
    public int TargetQualitySpan { get; set; } = 5;
    public int TargetMinimumQuality { get; set; } = 50;
    public bool TargetKeepSmallestOnUnmet { get; set; }
    public int DescreenLevel { get; set; } = 2;
    public bool WidthEnabled { get; set; } = true;
    public bool HeightEnabled { get; set; } = true;
    public string WidthOperator { get; set; } = ">=";
    public string HeightOperator { get; set; } = ">=";
    public int WidthValue { get; set; } = 1920;
    public int HeightValue { get; set; } = 1080;
    public string ResolutionJoin { get; set; } = "AND";
    public bool SameFolder { get; set; } = true;
    public string OutputDirectory { get; set; } = "";
    public bool ReplaceOriginal { get; set; }
    public string ArchiveEncoding { get; set; } = "auto";
    public bool PreserveNonImageFiles { get; set; } = true;
    public bool ReplaceSourceArchive { get; set; }
}

public sealed class WorkflowConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FromNodeId { get; set; } = "";
    public string FromPort { get; set; } = "out";
    public string ToNodeId { get; set; } = "";
    public string ToPort { get; set; } = "in";
}

public sealed class FileJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourcePath { get; set; } = "";
    public string Name => Path.GetFileName(SourcePath);
    public string Format { get; set; } = "";
    public string TargetFormat { get; set; } = "";
    public long OriginalSize { get; set; }
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public int TargetWidth { get; set; }
    public int TargetHeight { get; set; }
    public long? EstimatedSize { get; set; }
    public int? FinalQuality { get; set; }
    public string Status { get; set; } = "待运行";
    public bool Checked { get; set; } = true;
    public string? OutputPath { get; set; }
    public string OutputNodeId { get; set; } = "";
    public string ArchiveJobId { get; set; } = "";
    public string ArchiveEntryPath { get; set; } = "";
    public string OriginNodeId { get; set; } = "";
    public string OriginConnectionId { get; set; } = "";
    public List<string> RouteNodeIds { get; set; } = new();
    public List<string> RouteConnectionIds { get; set; } = new();
    public List<string> TargetSizeNotes { get; set; } = new();
    public string? OutputWarning { get; set; }
    [JsonIgnore]
    internal bool OutputReady { get; set; }
    [JsonIgnore]
    public long CurrentSize { get; set; }
    [JsonIgnore]
    public int CurrentWidth { get; set; }
    [JsonIgnore]
    public int CurrentHeight { get; set; }
    [JsonIgnore]
    public string OriginalSourcePath { get; set; } = "";
    [JsonIgnore]
    public bool SourceWasReplaced { get; set; }

    internal void ApplyExecutionResult(ExecutionResult result)
    {
        OutputReady = false;
        OutputPath = null;
        OutputWarning = null;
        TargetFormat = ImageMetadataReader.FormatName(result.FinalPath);
        TargetWidth = result.Width;
        TargetHeight = result.Height;
        EstimatedSize = result.Size;
        FinalQuality = result.FinalQuality;
        OutputNodeId = result.OutputNodeId;
        RouteNodeIds = result.RouteNodeIds.ToList();
        RouteConnectionIds = result.RouteConnectionIds.ToList();
        TargetSizeNotes = result.TargetSizeNotes.ToList();
    }

    internal void ApplySavedOutput(ExecutionResult result, ImageOutputResult saved, bool cacheHit)
    {
        OutputPath = saved.Path;
        EstimatedSize = saved.Size;
        OutputWarning = saved.Warning;
        Status = saved.Warning is not null
            ? saved.Replaced ? "已完成 · 保存有提示" : "已完成 · 原图未替换，结果已另存"
            : saved.Replaced ? "已完成 · 已替换原图" : cacheHit ? "已完成 · 使用预估缓存" : "已完成";
        if (saved.Replaced)
        {
            if (string.IsNullOrWhiteSpace(OriginalSourcePath)) OriginalSourcePath = SourcePath;
            SourceWasReplaced = true;
            SourcePath = saved.Path;
            CurrentSize = saved.Size;
            CurrentWidth = result.Width;
            CurrentHeight = result.Height;
        }
        OutputReady = true;
    }
}

public sealed class ArchiveJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NodeId { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string Name => Path.GetFileName(SourcePath);
    public long Size { get; set; }
    public string Status { get; set; } = "待预处理";
    public int Progress { get; set; }
    public int EntryCount { get; set; }
    public int ImageCount { get; set; }
    public string OutputDirectory { get; set; } = "";
    [JsonIgnore]
    public string PreparedFingerprint { get; set; } = "";
    [JsonIgnore]
    internal List<ArchiveEntryRecord> Entries { get; set; } = new();
    [JsonIgnore]
    internal bool OwnsOutputDirectory { get; set; }
    [JsonIgnore]
    internal bool SourceWasReplaced { get; set; }
}

internal sealed class ArchiveEntryRecord
{
    public string EntryPath { get; set; } = "";
    public string ExtractedPath { get; set; } = "";
    public bool IsImage { get; set; }
    public long Size { get; set; }
}

internal sealed class ExecutionState
{
    public string SourcePath { get; set; } = "";
    public string CurrentPath { get; set; } = "";
    public string TargetExtension { get; set; } = "";
    public int JpegQuality { get; set; } = 100;
    public bool AutoGrayscale { get; set; } = true;
    public int DescreenLevel { get; set; }
    public bool Transformed { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long Size { get; set; }
    public int RenderVersion { get; set; }
    public int MaterializedVersion { get; set; } = -1;
    public HashSet<string> VisitedNodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> RouteNodeIds { get; } = new();
    public List<string> RouteConnectionIds { get; } = new();
    public List<string> TargetSizeNotes { get; } = new();
    public List<string> TemporaryFiles { get; } = new();
}

internal sealed class ExecutionResult
{
    public string FinalPath { get; set; } = "";
    public string OutputNodeId { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Size { get; set; }
    public int FinalQuality { get; set; } = 100;
    public bool Transformed { get; set; }
    public List<string> RouteNodeIds { get; set; } = new();
    public List<string> RouteConnectionIds { get; set; } = new();
    public List<string> TargetSizeNotes { get; set; } = new();
    public List<string> TemporaryFiles { get; set; } = new();
}

internal sealed class EstimateCacheEntry
{
    public string Signature { get; set; } = "";
    public string ResultPath { get; set; } = "";
    public string OutputNodeId { get; set; } = "";
    public long Size { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int FinalQuality { get; set; } = 100;
    public List<string> RouteNodeIds { get; set; } = new();
    public List<string> RouteConnectionIds { get; set; } = new();
    public List<string> TargetSizeNotes { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public static EstimateCacheEntry FromResult(string signature, ExecutionResult result) => new()
    {
        Signature = signature,
        ResultPath = result.FinalPath,
        OutputNodeId = result.OutputNodeId,
        Size = result.Size,
        Width = result.Width,
        Height = result.Height,
        FinalQuality = result.FinalQuality,
        RouteNodeIds = result.RouteNodeIds.ToList(),
        RouteConnectionIds = result.RouteConnectionIds.ToList(),
        TargetSizeNotes = result.TargetSizeNotes.ToList()
    };

    public ExecutionResult RestoreResult(string sourcePath) => new()
    {
        FinalPath = ResultPath,
        OutputNodeId = OutputNodeId,
        Size = Size,
        Width = Width,
        Height = Height,
        FinalQuality = FinalQuality,
        RouteNodeIds = RouteNodeIds.ToList(),
        RouteConnectionIds = RouteConnectionIds.ToList(),
        TargetSizeNotes = TargetSizeNotes.ToList(),
        Transformed = !ResultPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
    };
}

public sealed class HostRequest
{
    public string Id { get; set; } = "";
    public string Command { get; set; } = "";
    public System.Text.Json.JsonElement Payload { get; set; }
}

public sealed class HostResponse
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public object? Data { get; set; }
    public string? Error { get; set; }
}

public sealed class HostEvent
{
    [JsonPropertyName("event")]
    public string EventName { get; set; } = "";
    public object? Data { get; set; }
}

public sealed class WorkSummary
{
    public int Total { get; set; }
    public int Successes { get; set; }
    public int Failures { get; set; }
    public int CacheHits { get; set; }
    public int Replaced { get; set; }
    public int Skipped { get; set; }
    public int PackedArchives { get; set; }
    public int ReplacedArchives { get; set; }
    public int ArchiveFailures { get; set; }
    public int CleanedExtractionFolders { get; set; }
    public bool Cancelled { get; set; }
}
