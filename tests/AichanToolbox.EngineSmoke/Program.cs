using System.Diagnostics;
using System.IO.Compression;
using AichanToolbox.Core;
using NetVips;

if (args.Length != 1) throw new InvalidOperationException("请传入项目根目录。");
var projectRoot = Path.GetFullPath(args[0]);
var temporaryRoot = Path.Combine(Path.GetTempPath(), "AichanEngineSmoke", Guid.NewGuid().ToString("N"));
var engineCache = Path.Combine(temporaryRoot, "cache");
Directory.CreateDirectory(temporaryRoot);

try
{
    var ffmpeg = Path.Combine(projectRoot, "vendor", "ffmpeg", "ffmpeg.exe");
    var jpegli = Path.Combine(projectRoot, "vendor", "jxl-v0.11.2-win-x64", "bin", "cjpegli.exe");
    var ppm = Path.Combine(temporaryRoot, "source.ppm");
    var source = Path.Combine(temporaryRoot, "source.png");
    WritePpm(ppm, 64, 32);
    await ConvertToPngAsync(ffmpeg, ppm, source);
    var engine = new ImageEngine(ffmpeg, jpegli, engineCache);
    engine.ValidateDependencies();
    Require(NetVips.Cache.Max == 0, "libvips 操作缓存没有关闭。");
    engine.ConfigureConcurrency(6);
    engine.ReadDimensions(source);
    Require(CanOpenExclusively(source), "读取分辨率后 libvips 仍占用源文件。");
    var runner = new WorkflowRunner(engine);
    Require(ShellRecycleBin.WorkerApartmentState == ApartmentState.STA, "回收站队列没有运行在专用 STA 线程中。");

    DesktopBridge.ValidateWorkflow(LinearWorkflow(Node("validated-convert", "ConvertJpg")));
    var unsafeOutputRejected = false;
    try { DesktopBridge.ValidateWorkflow(LinearWorkflow(Node("unsafe-resize", "Resize", data => data.ScalePercent = 80))); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("未生成 JPG", StringComparison.Ordinal)) { unsafeOutputRejected = true; }
    Require(unsafeOutputRejected, "未经过 JPG 输出节点的可达分支没有在运行前被拒绝。");
    var passthroughFilterWorkflow = FilterPassthroughWorkflow();
    DesktopBridge.ValidateWorkflow(passthroughFilterWorkflow);
    var untouchedPng = await runner.ExecuteAsync(Job(source, 64, 32), passthroughFilterWorkflow, CancellationToken.None);
    Require(!untouchedPng.Transformed, "符合筛选条件的 PNG 直通分支不应生成新文件。");
    Require(untouchedPng.FinalPath.Equals(source, StringComparison.OrdinalIgnoreCase), "不处理分支没有保留原始图片路径。");

    var first = await runner.ExecuteAsync(
        Job(source, 64, 32),
        LinearWorkflow(
            Node("convert", "ConvertJpg"),
            Node("resize", "Resize", data => data.ScalePercent = 50),
            Node("quality", "Quality", data => data.QualityPercent = 90)),
        CancellationToken.None);
    Require(first.Transformed, "PNG/PPM 转换工作流应标记为已处理。");
    Require(Path.GetExtension(first.FinalPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase), "最终格式不是 JPG。");
    Require(first.Width == 32 && first.Height == 16, $"缩放结果错误：{first.Width}×{first.Height}。");
    Require(first.RouteNodeIds.Count == 5 && first.RouteConnectionIds.Count == 4, "工作流没有记录完整的节点与连线路径。");
    Require(File.Exists(first.FinalPath) && first.Size > 0, "未生成 JPEG 文件。");
    Require(CanOpenExclusively(source), "工作流完成后 libvips 仍占用源文件。");
    using (var decoded = Image.NewFromFile(first.FinalPath))
    {
        Require(decoded.Width == 32 && decoded.Height == 16, "libvips 无法正确读取 Jpegli 输出。");
        Require(decoded.Bands >= 3, "彩色图片被黑白优化误判为灰度图。");
    }
    engine.ReadDimensions(first.FinalPath);
    Require(CanOpenExclusively(first.FinalPath), "读取 JPG 分辨率后 libvips 仍占用源文件。");
    Require(await Is444Async(ffmpeg, first.FinalPath), "JPEG 不是 4:4:4 色度采样。");

    var graySource = Path.Combine(temporaryRoot, "gray-source.ppm");
    WriteGrayPpm(graySource, 96, 64);
    var grayOptimized = await runner.ExecuteAsync(
        Job(graySource, 96, 64),
        LinearWorkflow(Node("gray-convert", "ConvertJpg")),
        CancellationToken.None);
    using (var decoded = Image.NewFromFile(grayOptimized.FinalPath))
        Require(decoded.Bands == 1, $"明确黑白图没有使用单通道 JPG 编码：{decoded.Bands} bands。");

    var grayDisabledWorkflow = LinearWorkflow(Node("gray-colour-convert", "ConvertJpg"));
    grayDisabledWorkflow.AutoGrayscale = false;
    var grayColour = await runner.ExecuteAsync(Job(graySource, 96, 64), grayDisabledWorkflow, CancellationToken.None);
    using (var decoded = Image.NewFromFile(grayColour.FinalPath))
        Require(decoded.Bands >= 3, "关闭黑白优化后仍被强制转换成了单通道 JPG。");

    var lowSaturationSource = Path.Combine(temporaryRoot, "low-saturation-source.ppm");
    WriteLowSaturationPpm(lowSaturationSource, 96, 64);
    var lowSaturation = await runner.ExecuteAsync(
        Job(lowSaturationSource, 96, 64),
        LinearWorkflow(Node("low-saturation-convert", "ConvertJpg")),
        CancellationToken.None);
    using (var decoded = Image.NewFromFile(lowSaturation.FinalPath))
        Require(decoded.Bands >= 3, "低饱和度彩色图片被黑白优化误判为灰度图。");

    var descreened = await runner.ExecuteAsync(
        Job(source, 64, 32),
        LinearWorkflow(
            Node("descreen", "Descreen", data => data.DescreenLevel = 2),
            Node("descreen-quality", "Quality", data => data.QualityPercent = 90)),
        CancellationToken.None);
    Require(descreened.Transformed && File.Exists(descreened.FinalPath), "逆网点化节点没有生成处理结果。");
    using (var decoded = Image.NewFromFile(descreened.FinalPath))
        Require(decoded.Bands == 1, "逆网点化结果没有按设计输出灰度图。");
    Require(CanOpenExclusively(source), "逆网点化完成后仍占用源文件。");

    var passthrough = await runner.ExecuteAsync(
        Job(first.FinalPath, 32, 16),
        LinearWorkflow(Node("convert", "ConvertJpg")),
        CancellationToken.None);
    Require(!passthrough.Transformed, "原生 JPG 经过转换节点时不应重复编码。");
    Require(passthrough.FinalPath.Equals(first.FinalPath, StringComparison.OrdinalIgnoreCase), "JPG 直通路径发生变化。");

    var exactSize = await runner.ExecuteAsync(
        Job(source, 64, 32),
        SizeBranchWorkflow(),
        CancellationToken.None);
    Require(exactSize.Width == 32 && exactSize.Height == 16, "按当前转换结果进行大小筛选后未进入匹配分支。");
    Require(exactSize.TemporaryFiles.Count >= 1, "大小筛选没有物化并登记可清理的精确预估结果。");

    var currentInput = Job(source, 64, 32);
    currentInput.OriginalWidth = 640;
    currentInput.OriginalHeight = 320;
    var currentResolution = await runner.ExecuteAsync(
        currentInput,
        ResolutionBranchWorkflow(),
        CancellationToken.None);
    Require(currentResolution.Width == 16 && currentResolution.Height == 8, "分辨率筛选没有按入口的当前结果判断。");
    Require(currentInput.OriginalWidth == 640 && currentInput.OriginalHeight == 320, "执行工作流时原始分辨率被覆盖。");

    var noisySource = Path.Combine(temporaryRoot, "target-size-source.ppm");
    WriteNoisePpm(noisySource, 256, 256);
    var targetBytes = (long)Math.Floor(0.08 * 1024 * 1024);
    var targetSized = await runner.ExecuteAsync(
        Job(noisySource, 256, 256),
        LinearWorkflow(Node("target-size", "TargetSize", data =>
        {
            data.TargetSizeMb = 0.08;
            data.TargetStartQuality = 90;
            data.TargetQualitySpan = 5;
            data.TargetMinimumQuality = 50;
        })),
        CancellationToken.None);
    Require(targetSized.Transformed, "目标体积节点没有处理超过目标的输入。");
    Require(Path.GetExtension(targetSized.FinalPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase), "目标体积节点没有生成 JPEG。");
    Require(targetSized.Size <= targetBytes, $"目标体积节点输出超标：{targetSized.Size} > {targetBytes}。");
    Require(targetSized.Width == 256 && targetSized.Height == 256, "目标体积节点意外改变了分辨率。");

    var targetPassThrough = await runner.ExecuteAsync(
        Job(graySource, 96, 64),
        LinearWorkflow(Node("target-size-small", "TargetSize", data => data.TargetSizeMb = 8)),
        CancellationToken.None);
    Require(targetPassThrough.Transformed, "目标体积节点没有把非 JPG 输入转换为 JPG。");
    Require(Path.GetExtension(targetPassThrough.FinalPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase), "目标体积节点的最终输出不是 JPG。");

    var lockedFile = Path.Combine(temporaryRoot, "transient-lock.jpg");
    File.Copy(first.FinalPath, lockedFile);
    var heldStream = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
    var waitStarted = Stopwatch.StartNew();
    var waitTask = Task.Run(() => DesktopBridge.WaitForExclusiveAccess(lockedFile));
    await Task.Delay(350);
    heldStream.Dispose();
    await waitTask;
    Require(waitStarted.ElapsedMilliseconds >= 300, "替换原文件前没有等待短暂文件占用。");

    var replacedChecked = Job(first.FinalPath, 32, 16);
    replacedChecked.SourceWasReplaced = true;
    replacedChecked.OriginalSourcePath = source;
    var replacedUnchecked = Job(first.FinalPath, 32, 16);
    replacedUnchecked.SourceWasReplaced = true;
    replacedUnchecked.Checked = false;
    var replacementConfirmation = DesktopBridge.GetReplacedInputJobs(new[] { replacedChecked, replacedUnchecked, Job(source, 64, 32) });
    Require(replacementConfirmation.Count == 1 && ReferenceEquals(replacementConfirmation[0], replacedChecked), "替换源文件运行前确认没有只筛选已勾选任务。");
    DesktopBridge.AcceptReplacementAsNewOriginal(replacedChecked);
    Require(!replacedChecked.SourceWasReplaced, "确认使用新图片后没有清除待确认标记。");
    Require(replacedChecked.OriginalSourcePath.Equals(replacedChecked.SourcePath, StringComparison.OrdinalIgnoreCase), "确认使用新图片后没有更新原始路径基线。");
    Require(replacedChecked.Format == "JPG", "确认使用新图片后没有更新原格式。");
    Require(replacedChecked.OriginalSize == new FileInfo(replacedChecked.SourcePath).Length, "确认使用新图片后没有更新原始大小。");
    Require(replacedChecked.OriginalWidth == 32 && replacedChecked.OriginalHeight == 16, "确认使用新图片后没有更新原始分辨率。");

    var archiveSource = Path.Combine(temporaryRoot, "image-pack.zip");
    using (var stream = new FileStream(archiveSource, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
    {
        var imageEntry = zip.CreateEntry("pages/001.png", CompressionLevel.NoCompression);
        using (var input = File.OpenRead(source))
        using (var output = imageEntry.Open()) input.CopyTo(output);
        var textEntry = zip.CreateEntry("readme.txt", CompressionLevel.NoCompression);
        using var writer = new StreamWriter(textEntry.Open());
        writer.Write("Aichan archive smoke test");
    }
    var archiveService = new ArchiveService();
    var archiveJob = new ArchiveJob { NodeId = "zip-extract", SourcePath = archiveSource, Size = new FileInfo(archiveSource).Length };
    await archiveService.ExtractAsync(archiveJob, "auto", null, null, CancellationToken.None);
    Require(Path.GetFileName(archiveJob.OutputDirectory) == "image-pack", "ZIP 没有解压到同名文件夹。");
    Require(archiveJob.EntryCount == 2 && archiveJob.ImageCount == 1, "ZIP 解压条目或图片计数错误。");
    Require(archiveJob.Entries.All(entry => File.Exists(entry.ExtractedPath)), "ZIP 解压结果不完整。");

    var packedArchive = Path.Combine(temporaryRoot, "image-pack-processed.zip");
    await archiveService.PackStoreAsync(
        archiveJob,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pages/001.png"] = first.FinalPath },
        true,
        packedArchive,
        null,
        CancellationToken.None);
    await ArchiveService.VerifyAsync(packedArchive, 2, CancellationToken.None);
    using (var packed = ZipFile.OpenRead(packedArchive))
    {
        Require(packed.GetEntry("pages/001.jpg") is not null, "重新打包时没有按处理结果修改图片扩展名。");
        Require(packed.GetEntry("readme.txt") is not null, "重新打包时没有保留非图片文件。");
        Require(packed.Entries.All(entry => entry.CompressedLength == entry.Length), "ZIP 后处理没有使用 Store/仅存储。");
    }

    var adoptedArchive = new ArchiveJob
    {
        SourcePath = packedArchive,
        Size = 1,
        Status = "打包完成 · 已替换原 ZIP",
        Progress = 100,
        EntryCount = 2,
        ImageCount = 1,
        OutputDirectory = archiveJob.OutputDirectory,
        PreparedFingerprint = "stale",
        OwnsOutputDirectory = true,
        SourceWasReplaced = true
    };
    adoptedArchive.Entries.AddRange(archiveJob.Entries);
    DesktopBridge.AcceptReplacementAsNewArchiveOriginal(adoptedArchive);
    Require(!adoptedArchive.SourceWasReplaced, "确认使用新 ZIP 后没有清除待确认标记。");
    Require(adoptedArchive.Size == new FileInfo(packedArchive).Length, "确认使用新 ZIP 后没有更新大小基线。");
    Require(adoptedArchive.EntryCount == 0 && adoptedArchive.ImageCount == 0 && adoptedArchive.Entries.Count == 0, "确认使用新 ZIP 后没有清除旧条目。");
    Require(string.IsNullOrEmpty(adoptedArchive.OutputDirectory) && !adoptedArchive.OwnsOutputDirectory, "确认使用新 ZIP 后没有释放旧解压目录状态。");
    Require(adoptedArchive.Status == "待重新预处理" && adoptedArchive.Progress == 0, "确认使用新 ZIP 后没有重置预处理状态。");

    var unmanagedDirectory = Path.Combine(temporaryRoot, "unmanaged");
    Directory.CreateDirectory(unmanagedDirectory);
    var unmanagedJob = new ArchiveJob { SourcePath = archiveSource, OutputDirectory = unmanagedDirectory };
    var unmanagedDeleteBlocked = false;
    try { archiveService.DeleteExtractionDirectory(unmanagedJob); }
    catch (InvalidOperationException) { unmanagedDeleteBlocked = true; }
    Require(unmanagedDeleteBlocked && Directory.Exists(unmanagedDirectory), "清理节点允许删除未登记的目录。");

    var extractedDirectory = archiveJob.OutputDirectory;
    Require(archiveJob.OwnsOutputDirectory, "解压结果没有登记为节点创建的目录。");
    archiveService.DeleteExtractionDirectory(archiveJob);
    Require(!Directory.Exists(extractedDirectory), "清理节点没有删除解压目录。");
    Require(string.IsNullOrEmpty(archiveJob.OutputDirectory) && !archiveJob.OwnsOutputDirectory, "清理完成后没有清除目录所有权状态。");

    var unsafeArchive = Path.Combine(temporaryRoot, "unsafe.zip");
    using (var stream = new FileStream(unsafeArchive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("../escaped.txt", CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open());
        writer.Write("must stay inside extraction root");
    }
    var unsafeJob = new ArchiveJob { NodeId = "zip-extract", SourcePath = unsafeArchive, Size = new FileInfo(unsafeArchive).Length };
    var traversalBlocked = false;
    try { await archiveService.ExtractAsync(unsafeJob, "auto", null, null, CancellationToken.None); }
    catch (InvalidDataException) { traversalBlocked = true; }
    Require(traversalBlocked && !File.Exists(Path.Combine(temporaryRoot, "escaped.txt")), "ZIP 路径穿越没有被阻止。");

    Console.WriteLine($"ENGINE_SMOKE_OK first={first.Size}B current-size={exactSize.Size}B target-size={targetSized.Size}B sampling=4:4:4 jpg-path-validation=true jpg-pass-through=true replacement-baseline=true recycle-sta=true archive-replacement-baseline=true zip-store=true zip-cleanup-safe=true zip-slip-blocked=true");
}
finally
{
    try { Directory.Delete(temporaryRoot, true); } catch { }
}

static FileJob Job(string path, int width, int height)
    => new()
    {
        SourcePath = path,
        Format = ImageMetadataReader.FormatName(path),
        OriginalWidth = width,
        OriginalHeight = height,
        OriginalSize = new FileInfo(path).Length,
        CurrentWidth = width,
        CurrentHeight = height,
        CurrentSize = new FileInfo(path).Length
    };

static WorkflowDocument LinearWorkflow(params WorkflowNode[] operations)
{
    var import = Node("import", "Import");
    var output = Node("output", "Output");
    var nodes = new[] { import }.Concat(operations).Append(output).ToList();
    var connections = new List<WorkflowConnection>();
    for (var index = 0; index < nodes.Count - 1; index++)
        connections.Add(Connection(nodes[index], "out", nodes[index + 1]));
    return new WorkflowDocument { Nodes = nodes, Connections = connections };
}

static WorkflowDocument SizeBranchWorkflow()
{
    var import = Node("import", "Import");
    var convert = Node("convert", "ConvertJpg");
    var filter = Node("size", "SizeFilter", data =>
    {
        data.SizeOperator = ">";
        data.SizeMb = 0;
    });
    var resize = Node("resize", "Resize", data => data.ScalePercent = 50);
    var output = Node("output", "Output");
    return new WorkflowDocument
    {
        Nodes = new() { import, convert, filter, resize, output },
        Connections = new()
        {
            Connection(import, "out", convert),
            Connection(convert, "out", filter),
            Connection(filter, "match", resize),
            Connection(resize, "out", output)
        }
    };
}

static WorkflowDocument FilterPassthroughWorkflow()
{
    var import = Node("import", "Import");
    var filter = Node("size", "SizeFilter", data =>
    {
        data.SizeOperator = "<=";
        data.SizeMb = 10;
    });
    var convert = Node("convert", "ConvertJpg");
    var output = Node("output", "Output");
    return new WorkflowDocument
    {
        Nodes = new() { import, filter, convert, output },
        Connections = new()
        {
            Connection(import, "out", filter),
            Connection(filter, "match", output),
            Connection(filter, "else", convert),
            Connection(convert, "out", output)
        }
    };
}

static WorkflowDocument ResolutionBranchWorkflow()
{
    var import = Node("import", "Import");
    var firstResize = Node("resize-first", "Resize", data => data.ScalePercent = 50);
    var filter = Node("resolution", "ResolutionFilter", data =>
    {
        data.WidthOperator = "<=";
        data.WidthValue = 32;
        data.HeightOperator = "<=";
        data.HeightValue = 16;
        data.ResolutionJoin = "AND";
    });
    var secondResize = Node("resize-second", "Resize", data => data.ScalePercent = 50);
    var output = Node("output", "Output");
    return new WorkflowDocument
    {
        Nodes = new() { import, firstResize, filter, secondResize, output },
        Connections = new()
        {
            Connection(import, "out", firstResize),
            Connection(firstResize, "out", filter),
            Connection(filter, "match", secondResize),
            Connection(secondResize, "out", output)
        }
    };
}

static WorkflowNode Node(string id, string type, Action<NodeSettings>? configure = null)
{
    var node = new WorkflowNode { Id = id, Type = type, Title = type };
    configure?.Invoke(node.Data);
    return node;
}

static WorkflowConnection Connection(WorkflowNode from, string port, WorkflowNode to)
    => new() { FromNodeId = from.Id, FromPort = port, ToNodeId = to.Id, ToPort = "in" };

static void WritePpm(string path, int width, int height)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        writer.Write((byte)(x * 255 / Math.Max(1, width - 1)));
        writer.Write((byte)(y * 255 / Math.Max(1, height - 1)));
        writer.Write((byte)((x + y) % 256));
    }
}

static void WriteNoisePpm(string path, int width, int height)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
    uint state = 0xA1C4_7E21;
    for (var index = 0; index < width * height * 3; index++)
    {
        state = state * 1664525u + 1013904223u;
        writer.Write((byte)(state >> 24));
    }
}

static void WriteGrayPpm(string path, int width, int height)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var value = (byte)((x * 5 + y * 3) % 256);
        writer.Write(value);
        writer.Write(value);
        writer.Write(value);
    }
}

static void WriteLowSaturationPpm(string path, int width, int height)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var value = (byte)(20 + (x * 3 + y * 2) % 220);
        writer.Write(value);
        writer.Write((byte)Math.Min(255, value + 2));
        writer.Write((byte)Math.Min(255, value + 4));
    }
}

static async Task<bool> Is444Async(string ffmpeg, string path)
{
    var start = new ProcessStartInfo(ffmpeg)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true
    };
    foreach (var argument in new[] { "-hide_banner", "-i", path, "-f", "null", "-" })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 FFmpeg 验证输出。");
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (await error).Contains("yuvj444p", StringComparison.OrdinalIgnoreCase);
}

static async Task ConvertToPngAsync(string ffmpeg, string input, string output)
{
    var start = new ProcessStartInfo(ffmpeg)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true
    };
    foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", input, output })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("无法生成 PNG 测试图片。");
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException(await error);
}

static bool CanOpenExclusively(string path)
{
    try
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        return stream.Length >= 0;
    }
    catch (IOException)
    {
        return false;
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
