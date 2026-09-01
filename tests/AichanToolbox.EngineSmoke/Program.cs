using System.Diagnostics;
using System.IO.Compression;
using AichanToolbox.Core;
using NetVips;

if (args.Length is < 1 or > 2) throw new InvalidOperationException("请传入项目根目录，可额外传入一张 HEIC/HEIF 样本验证缺少解码器的提示。");
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

    var recycleBatchRoot = Path.Combine(temporaryRoot, "recycle-batch");
    Directory.CreateDirectory(recycleBatchRoot);
    var recycleBatchFiles = Enumerable.Range(0, 128)
        .Select(index => Path.Combine(recycleBatchRoot, $"batch-{index:D3}.tmp"))
        .ToArray();
    foreach (var path in recycleBatchFiles) File.WriteAllText(path, path);
    var recycleTransactions = 0;
    var recycleBatchResults = ShellRecycleBin.DeleteFilesWithRetry(recycleBatchFiles, paths =>
    {
        recycleTransactions++;
        foreach (var path in paths) File.Delete(path);
    });
    Require(recycleTransactions == 1 && recycleBatchResults.All(value => value.Recycled),
        "128 个文件没有合并成一次回收站事务。");

    var retryBatchFiles = Enumerable.Range(0, 12)
        .Select(index => Path.Combine(recycleBatchRoot, $"retry-{index:D2}.tmp"))
        .ToArray();
    foreach (var path in retryBatchFiles) File.WriteAllText(path, path);
    var retryTransactions = 0;
    var retryBatchResults = ShellRecycleBin.DeleteFilesWithRetry(retryBatchFiles, paths =>
    {
        retryTransactions++;
        var count = retryTransactions == 1 ? paths.Count / 2 : paths.Count;
        foreach (var path in paths.Take(count)) File.Delete(path);
        if (retryTransactions == 1) throw new IOException("模拟批量事务部分完成。");
    });
    Require(retryTransactions == 2 && retryBatchResults.All(value => value.Recycled),
        "批量回收重试没有只处理第一次遗留的文件。");

    var noDecoderCache = Path.Combine(temporaryRoot, "no-heic-decoder-cache");
    var noHeicDecoder = new ImageEngine(Path.Combine(temporaryRoot, "missing-ffmpeg.exe"), jpegli, noDecoderCache);
    // 仅含容器标识，没有像素数据：验证尺寸读取失败时仍能导入并提示，而不是解码能力。
    var heicHeader = Convert.FromHexString("000000186674797068656963000000006D69663168656963");
    foreach (var extension in new[] { ".heic", ".HEIC", ".heif", ".HeIf" })
    {
        var heicPath = Path.Combine(temporaryRoot, "decoder-notice" + extension);
        File.WriteAllBytes(heicPath, heicHeader);
        Require(ArchiveService.IsSupportedImage(heicPath), "HEIC/HEIF 没有被导入或 ZIP 图片筛选识别。");
        var heicJob = DesktopBridge.CreateImageJob(heicPath, noHeicDecoder);
        Require(heicJob.SourcePath == heicPath && heicJob.Status == ImageEngine.MissingHeicDecoderMessage,
            "HEIC/HEIF 导入时没有保留文件并显示缺少解码器的提示。");
        Require(heicJob.OriginalWidth == 0 && heicJob.OriginalHeight == 0,
            "无法读取 HEIC 尺寸时不应伪造分辨率。");
        await RequireMissingHeicDecoderAsync(() => noHeicDecoder.RenderAsync(heicPath, ".jpg", 0, 0, 90, false, 0, CancellationToken.None));
        await RequireMissingHeicDecoderAsync(() => noHeicDecoder.PrepareJpegSourceAsync(heicPath, 0, 0, false, 0, CancellationToken.None));
        Require(File.ReadAllBytes(heicPath).SequenceEqual(heicHeader), "缺少解码器时修改了 HEIC 原文件。");
        Require(!engine.IsHeicDecoderMissing(heicPath), "已配置的可选 FFmpeg 回退被提前阻止。");
    }
    Require(!Directory.EnumerateFileSystemEntries(noDecoderCache).Any(), "缺少 HEIC 解码器时遗留了临时输出。");
    if (args.Length == 2)
    {
        var samplePath = Path.GetFullPath(args[1]);
        var originalBytes = File.ReadAllBytes(samplePath);
        var originalWriteTime = File.GetLastWriteTimeUtc(samplePath);
        var sampleJob = DesktopBridge.CreateImageJob(samplePath, noHeicDecoder);
        Require(sampleJob.Status == ImageEngine.MissingHeicDecoderMessage, "真实 HEIC 样本导入时没有显示缺少解码器的提示。");
        Require(sampleJob.OriginalWidth > 0 && sampleJob.OriginalHeight > 0, "真实 HEIC 样本未保留可读取的尺寸信息。");
        var sampleRunner = new WorkflowRunner(noHeicDecoder);
        await RequireMissingHeicDecoderAsync(() => sampleRunner.ExecuteAsync(
            sampleJob, LinearWorkflow(Node("heic-convert", "ConvertJpg")), CancellationToken.None));
        await RequireMissingHeicDecoderAsync(() => sampleRunner.ExecuteAsync(
            sampleJob, LinearWorkflow(Node("heic-target", "TargetSize", data => data.TargetSizeMb = 0.01)), CancellationToken.None));
        Require(File.ReadAllBytes(samplePath).SequenceEqual(originalBytes) && File.GetLastWriteTimeUtc(samplePath) == originalWriteTime,
            "缺少解码器提示验证修改了真实 HEIC 样本。");
        Console.WriteLine($"HEIC_SAMPLE_NOTICE_OK name={sampleJob.Name} dimensions={sampleJob.OriginalWidth}x{sampleJob.OriginalHeight} import=true convert=true target-size=true original-unchanged=true");
    }
    var pngWithoutCompatibilityDecoder = DesktopBridge.CreateImageJob(source, noHeicDecoder);
    Require(pngWithoutCompatibilityDecoder.Status == "待运行"
        && pngWithoutCompatibilityDecoder.OriginalWidth == 64 && pngWithoutCompatibilityDecoder.OriginalHeight == 32,
        "HEIC 提示影响了普通 PNG 导入。");
    var pngWithoutCompatibilityResult = await new WorkflowRunner(noHeicDecoder).ExecuteAsync(
        pngWithoutCompatibilityDecoder, LinearWorkflow(Node("no-ffmpeg-convert", "ConvertJpg")), CancellationToken.None);
    Require(pngWithoutCompatibilityResult.Transformed && File.Exists(pngWithoutCompatibilityResult.FinalPath),
        "没有可选 FFmpeg 时，HEIC 提示影响了普通 PNG 转 JPG。");

    DesktopBridge.ValidateWorkflow(LinearWorkflow(Node("validated-convert", "ConvertJpg")));
    var signatureWorkflow = LinearWorkflow(Node("signature-convert", "ConvertJpg"));
    var signatureJob = Job(source, 64, 32);
    var signatureBeforeLayout = DesktopBridge.BuildSignature(signatureJob, signatureWorkflow);
    signatureWorkflow.Nodes[0].X += 100;
    signatureWorkflow.Nodes[0].Width = 3200;
    signatureWorkflow.Nodes[0].Title = "文件列表";
    signatureWorkflow.Viewport.Zoom = 0.5;
    Require(DesktopBridge.BuildSignature(signatureJob, signatureWorkflow) == signatureBeforeLayout, "布局变化错误地清除了可复用的预估缓存。");
    signatureWorkflow.Connections[0].Id = Guid.NewGuid().ToString("N");
    Require(DesktopBridge.BuildSignature(signatureJob, signatureWorkflow) != signatureBeforeLayout, "重建连线后仍可能复用包含旧连线 ID 的预估路径。");
    var unsafeOutputRejected = false;
    try { DesktopBridge.ValidateWorkflow(LinearWorkflow(Node("unsafe-resize", "Resize", data => data.ScalePercent = 80))); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("未生成 JPG", StringComparison.Ordinal)) { unsafeOutputRejected = true; }
    Require(unsafeOutputRejected, "未经过 JPG 输出节点的可达分支没有在运行前被拒绝。");
    var passthroughFilterWorkflow = FilterPassthroughWorkflow();
    DesktopBridge.ValidateWorkflow(passthroughFilterWorkflow);
    var untouchedPng = await runner.ExecuteAsync(Job(source, 64, 32), passthroughFilterWorkflow, CancellationToken.None);
    Require(!untouchedPng.Transformed, "符合筛选条件的 PNG 直通分支不应生成新文件。");
    Require(untouchedPng.FinalPath.Equals(source, StringComparison.OrdinalIgnoreCase), "不处理分支没有保留原始图片路径。");
    Require(untouchedPng.FinalQuality == 100, "不处理分支的最终画质不是 100。");

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
    Require(first.FinalQuality == 90, $"画质节点的最终画质记录错误：{first.FinalQuality}。");
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
    Require(targetSized.FinalQuality is >= 50 and <= 95, $"目标体积节点没有记录实际命中的画质：{targetSized.FinalQuality}。");
    Require(targetSized.Width == 256 && targetSized.Height == 256, "目标体积节点意外改变了分辨率。");
    Require(targetSized.TargetSizeNotes.Count == 0, "达标结果不应带有保留最小结果的提示。");

    var missingUnmetWorkflow = LinearWorkflow(Node("missing-unmet", "TargetSize", data =>
    {
        data.TargetSizeMb = 8;
        data.TargetKeepSmallestOnUnmet = true;
    }));
    var missingUnmetRejected = false;
    try { DesktopBridge.ValidateWorkflow(missingUnmetWorkflow); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("未达标", StringComparison.Ordinal)
        && exception.Message.Contains("出口缺失", StringComparison.Ordinal)) { missingUnmetRejected = true; }
    Require(missingUnmetRejected, "勾选最小结果却未连接未达标出口时，没有在编码前拒绝启动。");
    missingUnmetWorkflow.Connections.Add(new WorkflowConnection { FromNodeId = "missing-unmet", FromPort = "unmet", ToNodeId = "missing-node" });
    var danglingUnmetRejected = false;
    try { DesktopBridge.ValidateWorkflow(missingUnmetWorkflow); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("出口缺失", StringComparison.Ordinal)) { danglingUnmetRejected = true; }
    Require(danglingUnmetRejected, "未达标出口连接到不存在的节点时没有被预检拒绝。");
    missingUnmetWorkflow.Nodes.Single(node => node.Id == "missing-unmet").Data.TargetKeepSmallestOnUnmet = false;
    missingUnmetWorkflow.Connections.RemoveAt(missingUnmetWorkflow.Connections.Count - 1);
    DesktopBridge.ValidateWorkflow(missingUnmetWorkflow);
    var unusedTargetWorkflow = LinearWorkflow(Node("used-convert", "ConvertJpg"));
    unusedTargetWorkflow.Nodes.Add(Node("unused-target", "TargetSize", data => data.TargetKeepSmallestOnUnmet = true));
    DesktopBridge.ValidateWorkflow(unusedTargetWorkflow);

    var targetPassThrough = await runner.ExecuteAsync(
        Job(graySource, 96, 64),
        LinearWorkflow(Node("target-size-small", "TargetSize", data => data.TargetSizeMb = 8)),
        CancellationToken.None);
    Require(targetPassThrough.Transformed, "目标体积节点没有把非 JPG 输入转换为 JPG。");
    Require(Path.GetExtension(targetPassThrough.FinalPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase), "目标体积节点的最终输出不是 JPG。");

    var targetUnmetBytes = (long)Math.Floor(0.01 * 1024 * 1024);
    var targetSkipWorkflow = TargetUnmetWorkflow(false);
    DesktopBridge.ValidateWorkflow(targetSkipWorkflow);
    var targetSkipped = await runner.ExecuteAsync(Job(noisySource, 256, 256), targetSkipWorkflow, CancellationToken.None);
    var targetSkipConnection = targetSkipWorkflow.Connections.Single(value => value.FromPort == "unmet");
    Require(!targetSkipped.Transformed, "关闭最小结果开关后，未达标分支仍修改了入口状态。");
    Require(targetSkipped.FinalPath.Equals(noisySource, StringComparison.OrdinalIgnoreCase), "关闭最小结果开关后，未达标分支没有跳过目标体积节点。");
    Require(targetSkipped.RouteConnectionIds.Contains(targetSkipConnection.Id), "目标体积未达标分支没有记录 unmet 连线。");
    Require(targetSkipped.TargetSizeNotes.Count == 0, "关闭最小结果开关后不应提示已保留最小结果。");

    var targetKeepWorkflow = TargetUnmetWorkflow(true);
    DesktopBridge.ValidateWorkflow(targetKeepWorkflow);
    var targetKept = await runner.ExecuteAsync(Job(noisySource, 256, 256), targetKeepWorkflow, CancellationToken.None);
    Require(targetKept.Transformed, "开启最小结果开关后，未达标分支没有保留编码结果。");
    Require(Path.GetExtension(targetKept.FinalPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase), "未达标分支保留的最小结果不是 JPG。");
    Require(targetKept.Size > targetUnmetBytes, "强制未达标测试意外达到了目标体积。");
    Require(targetKept.Size < new FileInfo(noisySource).Length, "未达标分支没有保留体积最小的真实编码候选。");
    Require(targetKept.RouteConnectionIds.Contains(targetKeepWorkflow.Connections.Single(value => value.FromPort == "unmet").Id), "保留最小结果后没有继续未达标分支。");
    Require(targetKept.TargetSizeNotes.Count == 1 && targetKept.TargetSizeNotes[0].Contains("已保留最小结果", StringComparison.Ordinal), "成功继续的未达标结果缺少非失败提示。");
    var keptCache = EstimateCacheEntry.FromResult("kept-result", targetKept);
    var keptFromCache = keptCache.RestoreResult(noisySource);
    Require(keptFromCache.FinalQuality == targetKept.FinalQuality
        && keptFromCache.TargetSizeNotes.SequenceEqual(targetKept.TargetSizeNotes)
        && keptFromCache.RouteConnectionIds.SequenceEqual(targetKept.RouteConnectionIds), "预估缓存丢失了未达标提示或执行路径。");
    keptFromCache.TargetSizeNotes.Clear();
    Require(keptCache.TargetSizeNotes.Count == 1 && targetKept.TargetSizeNotes.Count == 1, "缓存还原结果与缓存本身共享了可变提示列表。");

    var legacyTargetFailurePreserved = false;
    try
    {
        await runner.ExecuteAsync(
            Job(noisySource, 256, 256),
            LinearWorkflow(Node("legacy-target-unmet", "TargetSize", data =>
            {
                data.TargetSizeMb = 0.01;
                data.TargetMinimumQuality = 50;
            })),
            CancellationToken.None);
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("未达标", StringComparison.Ordinal))
    {
        legacyTargetFailurePreserved = true;
    }
    Require(legacyTargetFailurePreserved, "旧工作流未连接未达标出口时没有保留明确失败行为。");

    var fallbackWorkflow = TargetFallbackWorkflow();
    DesktopBridge.ValidateWorkflow(fallbackWorkflow);
    var targetFallback = await runner.ExecuteAsync(Job(noisySource, 256, 256), fallbackWorkflow, CancellationToken.None);
    Require(targetFallback.Width == 128 && targetFallback.Height == 128, "未达标分支没有进入二次按比例缩放。");
    Require(targetFallback.Size <= targetBytes, "二次缩放后的目标体积节点仍未达标。");
    Require(targetFallback.RouteConnectionIds.Contains(fallbackWorkflow.Connections.Single(value => value.FromNodeId == "target-unmet" && value.FromPort == "unmet").Id), "二次缩放工作流没有经过未达标出口。");

    var keptFallbackWorkflow = TargetFallbackWorkflow();
    keptFallbackWorkflow.Nodes.Single(node => node.Id == "target-unmet").Data.TargetKeepSmallestOnUnmet = true;
    DesktopBridge.ValidateWorkflow(keptFallbackWorkflow);
    var keptFallback = await runner.ExecuteAsync(Job(noisySource, 256, 256), keptFallbackWorkflow, CancellationToken.None);
    Require(keptFallback.Width == 128 && keptFallback.Height == 128 && keptFallback.Size <= targetBytes,
        "保留最小结果后没有继续下游缩放和再次目标体积压缩。");
    Require(keptFallback.TargetSizeNotes.Count == 1, "下游节点处理丢失了上游保留最小结果的提示。");

    var unsafeTargetSkipRejected = false;
    try { DesktopBridge.ValidateWorkflow(TargetUnmetAfterResizeWorkflow(false)); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("未生成 JPG", StringComparison.Ordinal)) { unsafeTargetSkipRejected = true; }
    Require(unsafeTargetSkipRejected, "跳过目标体积节点的未达标分支被错误地视为 JPG 输出。");
    DesktopBridge.ValidateWorkflow(TargetUnmetAfterResizeWorkflow(true));

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

    // Run the complete unmet-result path through saving and ZIP packaging.
    // Only the Windows recycle response is injected; image/ZIP bytes are real.
    var noisyJpeg = await engine.RenderAsync(noisySource, ".jpg", 256, 256, 100, false, 0, CancellationToken.None);
    var unmetArchivePath = Path.Combine(temporaryRoot, "unmet-output.zip");
    using (var zip = ZipFile.Open(unmetArchivePath, ZipArchiveMode.Create))
        zip.CreateEntryFromFile(noisyJpeg, "pages/001.jpg", CompressionLevel.NoCompression);
    var unmetArchive = new ArchiveJob { NodeId = "zip-extract", SourcePath = unmetArchivePath };
    await archiveService.ExtractAsync(unmetArchive, "auto", null, null, CancellationToken.None);
    var unmetEntry = unmetArchive.Entries.Single();
    var unmetJob = Job(unmetEntry.ExtractedPath, 256, 256);
    unmetJob.ArchiveJobId = unmetArchive.Id;
    unmetJob.ArchiveEntryPath = unmetEntry.EntryPath;
    var unmetOriginalBytes = File.ReadAllBytes(unmetJob.SourcePath);
    var saveWorkflow = TargetUnmetWorkflow(true);
    var saveNode = saveWorkflow.Nodes.Single(node => node.Type == "Output");
    saveNode.Data.ReplaceOriginal = true;
    var unmetOutput = await runner.ExecuteAsync(unmetJob, saveWorkflow, CancellationToken.None);
    Require(unmetOutput.TargetSizeNotes.Count == 1 && unmetOutput.Size > targetUnmetBytes, "保存链路测试未产生真实的未达标最小结果。");
    unmetJob.ApplyExecutionResult(unmetOutput);
    Require(unmetJob.EstimatedSize == unmetOutput.Size
        && unmetJob.FinalQuality == unmetOutput.FinalQuality
        && !unmetJob.OutputReady, "生成结果后没有立即显示实际体积和画质，或误认为已保存。");

    var recycleAttempts = 0;
    var interruptedRecycleWriter = new ImageOutputWriter(path => ShellRecycleBin.DeleteFileWithRetry(path, _ =>
    {
        recycleAttempts++;
        throw new OperationCanceledException("模拟 Windows 静默中止回收，工作流并未取消。");
    }));
    var savedUnmet = interruptedRecycleWriter.Write(unmetJob, unmetOutput, saveNode);
    unmetJob.ApplySavedOutput(unmetOutput, savedUnmet, false);
    Require(recycleAttempts == 3, "Windows 回收中止没有按文件操作失败重试。");
    Require(!savedUnmet.Replaced && !unmetJob.SourceWasReplaced && unmetJob.OutputReady, "另存成功后的输出状态错误。");
    Require(unmetJob.Status.StartsWith("已完成", StringComparison.Ordinal) && unmetJob.Status.Contains("另存", StringComparison.Ordinal), "回收站中止仍被标记为工作流已取消。");
    Require(unmetJob.OutputWarning?.Contains("原图未替换", StringComparison.Ordinal) == true, "回收失败另存结果后没有明确提示。");
    Require(File.ReadAllBytes(unmetJob.SourcePath).SequenceEqual(unmetOriginalBytes), "回收失败时覆盖或删除了原图。");
    Require(File.ReadAllBytes(savedUnmet.Path).SequenceEqual(File.ReadAllBytes(unmetOutput.FinalPath)), "另存的文件不是实际最小结果。");
    Require(unmetJob.EstimatedSize == new FileInfo(savedUnmet.Path).Length && unmetJob.EstimatedSize < unmetJob.OriginalSize,
        "预估大小没有显示实际保存的最小结果大小。");

    var outputIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { saveNode.Id };
    var unmetReplacements = DesktopBridge.BuildArchiveReplacements([unmetJob], outputIds);
    Require(unmetReplacements[unmetEntry.EntryPath] == savedUnmet.Path, "ZIP 没有选用另存的最小结果。");
    var unmetPackedPath = Path.Combine(temporaryRoot, "unmet-output-processed.zip");
    await archiveService.PackStoreAsync(unmetArchive, unmetReplacements, true, unmetPackedPath, null, CancellationToken.None);
    await ArchiveService.VerifyAsync(unmetPackedPath, 1, CancellationToken.None);
    using (var packed = ZipFile.OpenRead(unmetPackedPath))
    using (var entryStream = packed.GetEntry(unmetEntry.EntryPath)!.Open())
    using (var content = new MemoryStream())
    {
        entryStream.CopyTo(content);
        Require(content.ToArray().SequenceEqual(File.ReadAllBytes(unmetOutput.FinalPath)), "最终 ZIP 内仍是原图，而不是最小结果。");
        Require(content.Length == unmetJob.EstimatedSize, "ZIP 内的实际结果体积与文件列表显示不一致。");
    }

    var incompleteJob = Job(unmetEntry.ExtractedPath, 256, 256);
    incompleteJob.ArchiveEntryPath = unmetEntry.EntryPath;
    incompleteJob.ApplyExecutionResult(unmetOutput);
    foreach (var incompleteStatus in new[] { "已取消", "失败 · 无法保存", "正在处理" })
    {
        incompleteJob.Status = incompleteStatus;
        incompleteJob.OutputPath = savedUnmet.Path; // A stale path must not bypass completion checks.
        var incompleteBlocked = false;
        try { DesktopBridge.BuildArchiveReplacements([incompleteJob], outputIds); }
        catch (InvalidOperationException) { incompleteBlocked = true; }
        Require(incompleteBlocked, $"{incompleteStatus}的图片仍可回退原图进行 ZIP 打包。");
    }
    var realSavedPath = unmetJob.OutputPath;
    unmetJob.OutputPath = Path.Combine(temporaryRoot, "missing-processed-result.jpg");
    var missingResultBlocked = false;
    try { DesktopBridge.BuildArchiveReplacements([unmetJob], outputIds); }
    catch (FileNotFoundException) { missingResultBlocked = true; }
    Require(missingResultBlocked, "保存结果丢失后仍静默回退到原图。");
    unmetJob.OutputPath = realSavedPath;
    var unchangedJob = Job(unmetEntry.ExtractedPath, 256, 256);
    unchangedJob.ArchiveEntryPath = "pages/unchanged.jpg";
    unchangedJob.OutputNodeId = saveNode.Id;
    unchangedJob.OutputReady = true;
    unchangedJob.Status = "不处理";
    Require(DesktopBridge.BuildArchiveReplacements([unchangedJob], outputIds)[unchangedJob.ArchiveEntryPath] == unchangedJob.SourcePath,
        "正常不处理分支不再允许使用原图打包。");
    var uncheckedJob = Job(unmetEntry.ExtractedPath, 256, 256);
    uncheckedJob.Checked = false;
    Require(DesktopBridge.BuildArchiveReplacements([unmetJob, uncheckedJob], outputIds).Count == 1,
        "未勾选的图片错误阻止了其他结果打包。");

    var replaceCopy = Path.Combine(temporaryRoot, "successful-replacement.jpg");
    File.Copy(noisyJpeg, replaceCopy);
    var replaceJob = Job(replaceCopy, 256, 256);
    replaceJob.ApplyExecutionResult(unmetOutput);
    var successfulWriter = new ImageOutputWriter(File.Delete); // Only deletes this generated fixture.
    var replacedResult = successfulWriter.Write(replaceJob, unmetOutput, saveNode);
    replaceJob.ApplySavedOutput(unmetOutput, replacedResult, false);
    Require(replacedResult.Replaced && replaceJob.SourceWasReplaced && replaceJob.OutputReady, "正常替换原图的状态被破坏。");
    Require(replaceJob.EstimatedSize == unmetOutput.Size && File.ReadAllBytes(replaceCopy).SequenceEqual(File.ReadAllBytes(unmetOutput.FinalPath)),
        "正常替换原图时没有保存最小结果或大小错误。");

    var batchWriterTransactions = 0;
    var batchWriter = new ImageOutputWriter(recycleFiles: paths =>
    {
        batchWriterTransactions++;
        foreach (var path in paths) File.Delete(path);
        return paths.Select(path => new RecycleFileResult(path, true, null)).ToList();
    });
    var pendingWriterReplacements = new List<PendingImageReplacement>();
    for (var index = 0; index < 128; index++)
    {
        var sourcePath = Path.Combine(temporaryRoot, $"batch-replace-{index:D3}.jpg");
        File.Copy(noisyJpeg, sourcePath);
        var batchJob = Job(sourcePath, 256, 256);
        var prepared = batchWriter.Prepare(batchJob, unmetOutput, saveNode);
        Require(prepared.Completed is null && prepared.Replacement is not null,
            "替换原文件没有先进入安全暂存阶段。");
        pendingWriterReplacements.Add(prepared.Replacement!);
    }
    var batchWriterResults = batchWriter.CommitReplacements(pendingWriterReplacements);
    Require(batchWriterTransactions == 1 && batchWriterResults.Count == 128
        && batchWriterResults.All(value => value.Replaced && value.Warning is null && File.Exists(value.Path)
            && (File.GetAttributes(value.Path) & (FileAttributes.Hidden | FileAttributes.Temporary)) == 0),
        "128 张图片没有通过一次回收事务安全完成替换。");
    Require(batchWriterResults.All(value => File.ReadAllBytes(value.Path).SequenceEqual(File.ReadAllBytes(unmetOutput.FinalPath))),
        "批量替换后的文件不是对应的完整处理结果。");

    // Exercise the real Shell API immediately after both JPEG render paths.
    // An exclusive read can succeed while an undisposed libvips intermediate
    // still retains the source mapping and prevents Windows from recycling it.
    var recycleColourSource = Path.Combine(temporaryRoot, "recycle-colour-source.ppm");
    WriteNoisePpm(recycleColourSource, 960, 1280);
    var recycleColourJpeg = await engine.RenderAsync(recycleColourSource, ".jpg", 960, 1280, 95, false, 0, CancellationToken.None);
    var recycleGraySource = Path.Combine(temporaryRoot, "recycle-gray-source.ppm");
    WriteGrayPpm(recycleGraySource, 960, 1280);
    var recycleGrayJpeg = await engine.RenderAsync(recycleGraySource, ".jpg", 960, 1280, 95, false, 0, CancellationToken.None);
    var realRecycleWriter = new ImageOutputWriter();
    var realRecycleCases = new List<(string Name, bool TargetMinimum, FileJob Job, ExecutionResult Output, PendingImageReplacement Pending)>();
    foreach (var (name, jpeg) in new[] { ("colour", recycleColourJpeg), ("gray", recycleGrayJpeg) })
    foreach (var targetMinimum in new[] { false, true })
    {
        var recycleCopy = Path.Combine(temporaryRoot, $"回收测试 {name}-{targetMinimum}.jpg");
        File.Copy(jpeg, recycleCopy);
        var recycleJob = Job(recycleCopy, 960, 1280);
        var recycleWorkflow = targetMinimum
            ? TargetUnmetWorkflow(true)
            : LinearWorkflow(Node("recycle-quality", "Quality", data => data.QualityPercent = 90));
        var recycleSaveNode = recycleWorkflow.Nodes.Single(node => node.Type == "Output");
        recycleSaveNode.Data.ReplaceOriginal = true;
        var recycleOutput = await runner.ExecuteAsync(recycleJob, recycleWorkflow, CancellationToken.None);
        Require(recycleOutput.Transformed, "真实回收测试没有执行图片处理。");
        if (targetMinimum)
            Require(recycleOutput.TargetSizeNotes.Count == 1, "真实回收测试没有经过保留最小结果分支。");
        recycleJob.ApplyExecutionResult(recycleOutput);
        var prepared = realRecycleWriter.Prepare(recycleJob, recycleOutput, recycleSaveNode);
        Require(prepared.Completed is null && prepared.Replacement is not null,
            "真实回收测试没有进入批量暂存阶段。");
        realRecycleCases.Add((name, targetMinimum, recycleJob, recycleOutput, prepared.Replacement!));
    }
    var realRecycleResults = realRecycleWriter.CommitReplacements(realRecycleCases.Select(value => value.Pending).ToList());
    for (var index = 0; index < realRecycleCases.Count; index++)
    {
        var (name, targetMinimum, recycleJob, recycleOutput, _) = realRecycleCases[index];
        var recycled = realRecycleResults[index];
        recycleJob.ApplySavedOutput(recycleOutput, recycled, false);
        Require(recycled.Replaced && recycled.Warning is null,
            $"黑白优化后原图仍被占用（{name}，最小结果={targetMinimum}）：{recycled.Warning}");
        Require(recycleJob.OutputReady && recycleJob.SourceWasReplaced && recycleJob.EstimatedSize == recycleOutput.Size
            && File.ReadAllBytes(recycled.Path).SequenceEqual(File.ReadAllBytes(recycleOutput.FinalPath)),
            "真实回收替换没有使用实际处理结果或丢失了输出状态。");
    }

    var transientRecycle = Path.Combine(temporaryRoot, "transient-recycle.jpg");
    File.Copy(noisyJpeg, transientRecycle);
    var transientAttempts = 0;
    ShellRecycleBin.DeleteFileWithRetry(transientRecycle, path =>
    {
        if (++transientAttempts < 3) throw new IOException("模拟短暂回收故障。");
        File.Delete(path);
    });
    Require(transientAttempts == 3 && !File.Exists(transientRecycle), "短暂回收故障没有在重试后恢复。");

    var failedSaveJob = Job(unmetEntry.ExtractedPath, 256, 256);
    failedSaveJob.ApplyExecutionResult(unmetOutput);
    var invalidOutputNode = Node("invalid-save", "Output", data =>
    {
        data.SameFolder = false;
        data.OutputDirectory = failedSaveJob.SourcePath; // An existing file cannot be an output directory.
    });
    var realSaveFailure = false;
    try { successfulWriter.Write(failedSaveJob, unmetOutput, invalidOutputNode); }
    catch (IOException) { realSaveFailure = true; }
    Require(realSaveFailure && !failedSaveJob.OutputReady && failedSaveJob.EstimatedSize == unmetOutput.Size,
        "真正保存失败时误报成功，或丢失了已生成结果的实际大小。");
    Require(File.ReadAllBytes(failedSaveJob.SourcePath).SequenceEqual(unmetOriginalBytes), "保存失败损坏了原图。");
    using (var userCancellation = new CancellationTokenSource())
    {
        userCancellation.Cancel();
        var userCancelHonored = false;
        try { await runner.ExecuteAsync(failedSaveJob, saveWorkflow, userCancellation.Token); }
        catch (OperationCanceledException) when (userCancellation.IsCancellationRequested) { userCancelHonored = true; }
        Require(userCancelHonored, "真正的工作流取消被错误吞掉。");
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

    Console.WriteLine($"ENGINE_SMOKE_OK first={first.Size}B current-size={exactSize.Size}B target-size={targetSized.Size}B heic-missing-decoder=true heic-case-insensitive=true heic-original-preserved=true png-without-ffmpeg=true target-unmet-skip=true target-unmet-smallest=true target-unmet-retry=true target-unmet-legacy=true sampling=4:4:4 jpg-path-validation=true jpg-pass-through=true replacement-baseline=true recycle-sta=true recycle-batch=128-in-1 recycle-after-grayscale=true recycle-abort-fallback=true smallest-output-zip=true incomplete-output-blocked=true archive-replacement-baseline=true zip-store=true zip-cleanup-safe=true zip-slip-blocked=true");
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

static WorkflowDocument TargetUnmetWorkflow(bool keepSmallest)
{
    var import = Node("import", "Import");
    var target = Node("target-unmet", "TargetSize", data =>
    {
        data.TargetSizeMb = 0.01;
        data.TargetStartQuality = 90;
        data.TargetQualitySpan = 5;
        data.TargetMinimumQuality = 50;
        data.TargetKeepSmallestOnUnmet = keepSmallest;
    });
    var output = Node("output", "Output");
    return new WorkflowDocument
    {
        Nodes = new() { import, target, output },
        Connections = new()
        {
            Connection(import, "out", target),
            Connection(target, "unmet", output)
        }
    };
}

static WorkflowDocument TargetFallbackWorkflow()
{
    var import = Node("import", "Import");
    var firstTarget = Node("target-unmet", "TargetSize", data =>
    {
        data.TargetSizeMb = 0.01;
        data.TargetMinimumQuality = 50;
        data.TargetKeepSmallestOnUnmet = false;
    });
    var resize = Node("target-retry-resize", "Resize", data => data.ScalePercent = 50);
    var secondTarget = Node("target-retry", "TargetSize", data =>
    {
        data.TargetSizeMb = 0.08;
        data.TargetMinimumQuality = 50;
    });
    var output = Node("output", "Output");
    return new WorkflowDocument
    {
        Nodes = new() { import, firstTarget, resize, secondTarget, output },
        Connections = new()
        {
            Connection(import, "out", firstTarget),
            Connection(firstTarget, "unmet", resize),
            Connection(resize, "out", secondTarget),
            Connection(secondTarget, "out", output)
        }
    };
}

static WorkflowDocument TargetUnmetAfterResizeWorkflow(bool keepSmallest)
{
    var import = Node("import", "Import");
    var resize = Node("resize", "Resize", data => data.ScalePercent = 80);
    var target = Node("target-unmet", "TargetSize", data => data.TargetKeepSmallestOnUnmet = keepSmallest);
    var output = Node("output", "Output");
    return new WorkflowDocument
    {
        Nodes = new() { import, resize, target, output },
        Connections = new()
        {
            Connection(import, "out", resize),
            Connection(resize, "out", target),
            Connection(target, "unmet", output)
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

static async Task RequireMissingHeicDecoderAsync(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (InvalidOperationException exception)
    {
        var wrapped = new InvalidOperationException("图片处理失败。", exception);
        Require(DesktopBridge.FriendlyMessage(wrapped) == ImageEngine.MissingHeicDecoderMessage,
            "HEIC 错误在界面中没有保留缺少解码器的提示。");
        return;
    }
    throw new InvalidOperationException("缺少 HEIC 解码器时仍尝试处理图片。");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
