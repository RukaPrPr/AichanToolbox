using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace AichanToolbox.Core;

internal sealed class DesktopBridge : IDisposable
{
    private readonly Window _owner;
    private readonly WebView2 _browser;
    private readonly Action _beginWindowDrag;
    private readonly Action<string> _beginWindowResize;
    private readonly Action _appReady;
    private readonly AppearanceSettings _appearance;
    private readonly Action<ThemeSelection> _setTheme;
    private readonly JsonSerializerOptions _json;
    private readonly List<FileJob> _jobs = new();
    private readonly List<ArchiveJob> _archives = new();
    private readonly ConcurrentDictionary<string, EstimateCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cacheParent;
    private readonly string _cacheRoot;
    private readonly string _profilesPath;
    private readonly object _profileGate = new();
    private readonly ImageEngine _imageEngine;
    private readonly WorkflowRunner _runner;
    private readonly ArchiveService _archiveService = new();
    private CancellationTokenSource? _workCancellation;
    private string? _lastOutputDirectory;
    private readonly ImageOutputWriter _outputWriter = new();

    public DesktopBridge(Window owner, WebView2 browser, Action beginWindowDrag, Action<string> beginWindowResize,
        Action appReady, AppearanceSettings appearance, Action<ThemeSelection> setTheme)
    {
        _owner = owner;
        _browser = browser;
        _beginWindowDrag = beginWindowDrag;
        _beginWindowResize = beginWindowResize;
        _appReady = appReady;
        _appearance = appearance;
        _setTheme = setTheme;
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        _cacheParent = Path.Combine(AppContext.BaseDirectory, "Cache");
        _cacheRoot = Path.Combine(_cacheParent, Environment.ProcessId + "_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(_cacheRoot);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException("无法在软件目录创建 Cache 文件夹。请将软件完整解压到有写入权限的目录后再运行。", exception);
        }
        var appDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AichanToolbox");
        Directory.CreateDirectory(appDataRoot);
        _profilesPath = Path.Combine(appDataRoot, "workflow-profiles.json");
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        var jpegli = Path.Combine(AppContext.BaseDirectory, "tools", "cjpegli.exe");
        _imageEngine = new ImageEngine(ffmpeg, jpegli, _cacheRoot);
        _runner = new WorkflowRunner(_imageEngine);
        _ = Task.Run(() => CleanupStaleCacheSessions(_cacheParent, _cacheRoot));
    }

    public async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        HostRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<HostRequest>(eventArgs.WebMessageAsJson, _json)
                ?? throw new InvalidOperationException("收到空请求。");
            var data = await DispatchAsync(request, eventArgs.AdditionalObjects);
            Reply(new HostResponse { Id = request.Id, Ok = true, Data = data });
        }
        catch (Exception exception)
        {
            Reply(new HostResponse
            {
                Id = request?.Id ?? "",
                Ok = false,
                Error = FriendlyMessage(exception)
            });
        }
    }

    private async Task<object?> DispatchAsync(HostRequest request, IReadOnlyList<object>? additionalObjects)
    {
        switch (request.Command)
        {
            case "app.startup":
                return BuildStartupSnapshot(ReadOptionalString(request.Payload, "rememberedProfile"));
            case "app.setTheme":
                _setTheme(new ThemeSelection(
                    ReadString(request.Payload, "id"),
                    ReadString(request.Payload, "colorScheme"),
                    ReadString(request.Payload, "background")));
                return new { theme = _appearance.Current.Id };
            case "app.ready":
                return new { version = "8.1.1", jobs = _jobs, archives = _archives, processorCount = Environment.ProcessorCount, maximized = _owner.WindowState == WindowState.Maximized };
            case "app.frontendReady":
                CaptureFrontendStartupMetrics(request.Payload);
                _appReady();
                return null;
            case "window.drag":
                _beginWindowDrag();
                return null;
            case "window.resize":
                _beginWindowResize(ReadString(request.Payload, "edge"));
                return null;
            case "window.minimize":
                _owner.WindowState = WindowState.Minimized;
                return null;
            case "window.maximize":
                _owner.WindowState = _owner.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return new { maximized = _owner.WindowState == WindowState.Maximized };
            case "window.close":
                _owner.Close();
                return null;
            case "files.select":
                return SelectFiles();
            case "files.drop":
                AddFiles((additionalObjects ?? Array.Empty<object>())
                    .OfType<CoreWebView2File>()
                    .Select(file => file.Path));
                return _jobs;
            case "files.clear":
                ClearFiles();
                return _jobs;
            case "files.remove":
                RemoveFiles(ReadStringArray(request.Payload, "ids"));
                return _jobs;
            case "files.check":
                SetChecked(ReadString(request.Payload, "id"), ReadBoolean(request.Payload, "checked"));
                return null;
            case "files.checkAll":
                SetAllChecked(ReadBoolean(request.Payload, "checked"));
                return null;
            case "archives.select":
                return SelectArchives(ReadString(request.Payload, "nodeId"));
            case "archives.drop":
                AddArchives(
                    ReadString(request.Payload, "nodeId"),
                    (additionalObjects ?? Array.Empty<object>()).OfType<CoreWebView2File>().Select(file => file.Path));
                return _archives;
            case "archives.clear":
                ClearArchives(ReadString(request.Payload, "nodeId"));
                return _archives;
            case "archives.remove":
                RemoveArchives(ReadStringArray(request.Payload, "ids"));
                return _archives;
            case "archives.preflight":
                return ArchivePreflight(ReadWorkflowProperty(request.Payload, "workflow"));
            case "archives.preprocess":
            {
                var workRequest = ReadWorkRequest(request.Payload);
                await PreprocessOnlyAsync(workRequest.Workflow, ReadOptionalString(request.Payload, "nodeId"), workRequest.ArchivePasswords);
                return new { archives = _archives, jobs = _jobs };
            }
            case "dialog.outputDirectory":
                return ChooseOutputDirectory(ReadOptionalString(request.Payload, "current"));
            case "workflow.save":
                SaveWorkflow(ReadWorkflow(request.Payload));
                return null;
            case "workflow.load":
                return LoadWorkflow();
            case "profiles.list":
                return ListProfiles();
            case "profiles.save":
                SaveProfile(ReadString(request.Payload, "name"), ReadWorkflowProperty(request.Payload, "workflow"));
                return ListProfiles();
            case "profiles.load":
                return LoadProfile(ReadString(request.Payload, "name"));
            case "profiles.rename":
                RenameProfile(ReadString(request.Payload, "oldName"), ReadString(request.Payload, "newName"));
                return ListProfiles();
            case "profiles.delete":
                DeleteProfile(ReadString(request.Payload, "name"));
                return ListProfiles();
            case "work.validate":
                ValidateWorkflow(ReadWorkflowProperty(request.Payload, "workflow"));
                return null;
            case "work.confirmReplacedSources":
                return ConfirmReplacedSources(
                    ReadString(request.Payload, "mode"),
                    ReadBoolean(request.Payload, "willReplaceAgain"));
            case "work.acceptReplacedSources":
                return AcceptReplacedSources(ReadStringArray(request.Payload, "ids"));
            case "work.confirmReplacedArchives":
                return ConfirmReplacedArchives(
                    ReadWorkflowProperty(request.Payload, "workflow"),
                    ReadString(request.Payload, "mode"),
                    ReadBoolean(request.Payload, "willReplaceAgain"));
            case "work.acceptReplacedArchives":
                return AcceptReplacedArchives(ReadStringArray(request.Payload, "ids"));
            case "work.estimate":
            {
                var workRequest = ReadWorkRequest(request.Payload);
                return await StartWorkAsync(workRequest.Workflow, true, workRequest.UseReplacedSources, workRequest.PreprocessArchives, workRequest.ArchivePasswords);
            }
            case "work.run":
            {
                var workRequest = ReadWorkRequest(request.Payload);
                return await StartWorkAsync(workRequest.Workflow, false, workRequest.UseReplacedSources, workRequest.PreprocessArchives, workRequest.ArchivePasswords);
            }
            case "work.cancel":
                _workCancellation?.Cancel();
                return null;
            case "output.open":
                OpenOutputDirectory();
                return null;
            default:
                throw new InvalidOperationException($"未知命令：{request.Command}");
        }
    }

    private IReadOnlyList<FileJob> SelectFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要加入工作流的图片",
            Multiselect = true,
            Filter = "支持的图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.avif;*.heic;*.heif|所有文件|*.*"
        };
        if (dialog.ShowDialog(_owner) == true) AddFiles(dialog.FileNames);
        return _jobs;
    }

    private IReadOnlyList<ArchiveJob> SelectArchives(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) throw new InvalidOperationException("ZIP 解压节点无效。");
        var dialog = new OpenFileDialog
        {
            Title = "选择要批量解压的 ZIP",
            Multiselect = true,
            Filter = "ZIP 压缩包|*.zip|所有文件|*.*"
        };
        if (dialog.ShowDialog(_owner) == true) AddArchives(nodeId, dialog.FileNames);
        return _archives;
    }

    private void AddArchives(string nodeId, IEnumerable<string> paths)
    {
        var existing = _archives
            .Where(job => job.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            .Select(job => job.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!File.Exists(path)
                || !Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                || existing.Contains(path)) continue;
            _archives.Add(new ArchiveJob
            {
                NodeId = nodeId,
                SourcePath = Path.GetFullPath(path),
                Size = new FileInfo(path).Length
            });
            existing.Add(path);
        }
        Emit("archivesChanged", _archives);
    }

    private void ClearArchives(string nodeId)
    {
        var ids = _archives
            .Where(job => string.IsNullOrWhiteSpace(nodeId) || job.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            .Select(job => job.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveArchiveJobs(ids);
        _archives.RemoveAll(job => ids.Contains(job.Id));
        Emit("archivesChanged", _archives);
        Emit("jobsChanged", _jobs);
    }

    private void RemoveArchives(IEnumerable<string> ids)
    {
        var values = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveArchiveJobs(values);
        _archives.RemoveAll(job => values.Contains(job.Id));
        Emit("archivesChanged", _archives);
        Emit("jobsChanged", _jobs);
    }

    private void RemoveArchiveJobs(IReadOnlySet<string> archiveIds)
    {
        foreach (var job in _jobs.Where(job => archiveIds.Contains(job.ArchiveJobId)).ToList()) RemoveCache(job.Id);
        _jobs.RemoveAll(job => archiveIds.Contains(job.ArchiveJobId));
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var existing = _jobs.Select(job => job.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!File.Exists(path) || existing.Contains(path) || !IsSupportedImage(path)) continue;
            try
            {
                var info = new FileInfo(path);
                var dimensions = _imageEngine.ReadDimensions(path);
                _jobs.Add(new FileJob
                {
                    SourcePath = path,
                    OriginalSourcePath = path,
                    Format = ImageMetadataReader.FormatName(path),
                    OriginalSize = info.Length,
                    OriginalWidth = dimensions.Width,
                    OriginalHeight = dimensions.Height,
                    CurrentSize = info.Length,
                    CurrentWidth = dimensions.Width,
                    CurrentHeight = dimensions.Height,
                    TargetFormat = ImageMetadataReader.FormatName(path),
                    TargetWidth = dimensions.Width,
                    TargetHeight = dimensions.Height
                });
                existing.Add(path);
            }
            catch { }
        }
        Emit("jobsChanged", _jobs);
    }

    private static bool IsSupportedImage(string path)
        => ArchiveService.IsSupportedImage(path);

    private void ClearFiles()
    {
        foreach (var job in _jobs) RemoveCache(job.Id);
        _jobs.Clear();
        Emit("jobsChanged", _jobs);
    }

    private void RemoveFiles(IEnumerable<string> ids)
    {
        var values = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in values) RemoveCache(id);
        _jobs.RemoveAll(job => values.Contains(job.Id));
        Emit("jobsChanged", _jobs);
    }

    private void SetChecked(string id, bool value)
    {
        var job = _jobs.FirstOrDefault(item => item.Id == id);
        if (job is not null) job.Checked = value;
    }

    private void SetAllChecked(bool value)
    {
        foreach (var job in _jobs) job.Checked = value;
        Emit("jobsChanged", _jobs);
    }

    private string? ChooseOutputDirectory(string? current)
    {
        return ShellFolderPicker.Show(_owner, "选择工作流输出目录", current);
    }

    private void SaveWorkflow(WorkflowDocument workflow)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存工作流",
            Filter = "艾酱工作流|*.aiflow|JSON 文件|*.json",
            DefaultExt = ".aiflow",
            AddExtension = true,
            FileName = "艾酱图片工作流.aiflow"
        };
        if (dialog.ShowDialog(_owner) != true) return;
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(workflow, new JsonSerializerOptions(_json) { WriteIndented = true }), new UTF8Encoding(false));
    }

    private WorkflowDocument? LoadWorkflow()
    {
        var dialog = new OpenFileDialog
        {
            Title = "加载工作流",
            Filter = "艾酱工作流|*.aiflow;*.json|所有文件|*.*"
        };
        if (dialog.ShowDialog(_owner) != true) return null;
        var content = File.ReadAllText(dialog.FileName, Encoding.UTF8).TrimStart();
        var workflow = content.StartsWith("<", StringComparison.Ordinal)
            ? LegacyWorkflowReader.Read(dialog.FileName)
            : JsonSerializer.Deserialize<WorkflowDocument>(content, _json);
        return workflow ?? throw new InvalidOperationException("无法读取工作流文件。");
    }

    private IReadOnlyList<string> ListProfiles()
    {
        lock (_profileGate)
            return ReadProfiles().Keys.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private object BuildStartupSnapshot(string? rememberedProfile)
    {
        StartupTelemetry.Mark("profiles.startupSnapshot.start");
        string[] names;
        string selectedProfile = "";
        WorkflowDocument? workflow = null;
        lock (_profileGate)
        {
            var profiles = ReadProfiles();
            names = profiles.Keys.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (!string.IsNullOrWhiteSpace(rememberedProfile))
            {
                selectedProfile = names.FirstOrDefault(name => name.Equals(rememberedProfile, StringComparison.CurrentCultureIgnoreCase)) ?? "";
                if (selectedProfile.Length > 0) workflow = profiles[selectedProfile];
            }
        }
        StartupTelemetry.Mark("profiles.startupSnapshot.complete");
        return new
        {
            version = "8.1.1",
            theme = _appearance.Current.Id,
            jobs = _jobs,
            archives = _archives,
            processorCount = Environment.ProcessorCount,
            maximized = _owner.WindowState == WindowState.Maximized,
            profiles = names,
            selectedProfile,
            workflow
        };
    }

    private void SaveProfile(string name, WorkflowDocument workflow)
    {
        name = NormalizeProfileName(name);
        lock (_profileGate)
        {
            var profiles = ReadProfiles();
            profiles[name] = workflow;
            WriteProfiles(profiles);
        }
    }

    private WorkflowDocument LoadProfile(string name)
    {
        name = NormalizeProfileName(name);
        lock (_profileGate)
        {
            var profiles = ReadProfiles();
            return profiles.TryGetValue(name, out var workflow)
                ? workflow
                : throw new InvalidOperationException($"找不到工作流配置“{name}”。");
        }
    }

    private void RenameProfile(string oldName, string newName)
    {
        oldName = NormalizeProfileName(oldName);
        newName = NormalizeProfileName(newName);
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;
        lock (_profileGate)
        {
            var profiles = ReadProfiles();
            if (!profiles.TryGetValue(oldName, out var workflow))
                throw new InvalidOperationException($"找不到工作流配置“{oldName}”。");
            if (!string.Equals(oldName, newName, StringComparison.CurrentCultureIgnoreCase) && profiles.ContainsKey(newName))
                throw new InvalidOperationException($"工作流配置“{newName}”已经存在。");
            profiles.Remove(oldName);
            profiles[newName] = workflow;
            WriteProfiles(profiles);
        }
    }

    private void DeleteProfile(string name)
    {
        name = NormalizeProfileName(name);
        lock (_profileGate)
        {
            var profiles = ReadProfiles();
            if (!profiles.Remove(name)) throw new InvalidOperationException($"找不到工作流配置“{name}”。");
            WriteProfiles(profiles);
        }
    }

    private Dictionary<string, WorkflowDocument> ReadProfiles()
    {
        if (!File.Exists(_profilesPath)) return new Dictionary<string, WorkflowDocument>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            var content = File.ReadAllText(_profilesPath, Encoding.UTF8);
            var stored = JsonSerializer.Deserialize<Dictionary<string, WorkflowDocument>>(content, _json)
                ?? new Dictionary<string, WorkflowDocument>();
            return new Dictionary<string, WorkflowDocument>(stored, StringComparer.CurrentCultureIgnoreCase);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("内置工作流配置库已损坏，请备份并删除 workflow-profiles.json 后重试。", exception);
        }
    }

    private void WriteProfiles(Dictionary<string, WorkflowDocument> profiles)
    {
        var temporaryPath = _profilesPath + ".tmp";
        var content = JsonSerializer.Serialize(profiles, new JsonSerializerOptions(_json) { WriteIndented = true });
        File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
        File.Move(temporaryPath, _profilesPath, true);
    }

    private static string NormalizeProfileName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("请输入工作流配置名称。");
        if (name.Length > 40) throw new InvalidOperationException("工作流配置名称不能超过 40 个字符。");
        if (name.Any(char.IsControl)) throw new InvalidOperationException("工作流配置名称不能包含控制字符。");
        return name;
    }

    private object ArchivePreflight(WorkflowDocument workflow)
    {
        var nodes = ConnectedArchiveNodes(workflow);
        var nodeIds = nodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = _archives.Where(job => nodeIds.Contains(job.NodeId)).ToList();
        var missingNodes = nodes.Count(node => selected.All(job => !job.NodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)));
        var pending = selected.Count(job => !_archiveService.IsPrepared(job));
        return new
        {
            connected = nodes.Count,
            archives = selected.Count,
            pending,
            missingNodes,
            required = pending > 0 || missingNodes > 0
        };
    }

    private async Task PreprocessOnlyAsync(
        WorkflowDocument workflow,
        string? requestedNodeId,
        IReadOnlyDictionary<string, string> passwords)
    {
        if (_workCancellation is not null) throw new InvalidOperationException("已有任务正在运行。");
        ValidateWorkflow(workflow);
        var nodes = ConnectedArchiveNodes(workflow);
        if (!string.IsNullOrWhiteSpace(requestedNodeId))
            nodes = nodes.Where(node => node.Id.Equals(requestedNodeId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (nodes.Count == 0) throw new InvalidOperationException("ZIP 解压节点尚未连接到导入节点。");

        _workCancellation = new CancellationTokenSource();
        var summary = new WorkSummary();
        Emit("workState", new { busy = true, mode = "preprocess", stage = "preprocess", total = nodes.Sum(node => _archives.Count(job => job.NodeId == node.Id)) });
        try
        {
            await PrepareArchiveNodesAsync(workflow, nodes, passwords, _workCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            summary.Cancelled = true;
        }
        finally
        {
            _workCancellation.Dispose();
            _workCancellation = null;
            Emit("workState", new { busy = false, mode = "preprocess", stage = "preprocess", summary });
        }
    }

    private async Task PrepareArchiveNodesAsync(
        WorkflowDocument workflow,
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyDictionary<string, string> passwords,
        CancellationToken cancellationToken)
    {
        var importIds = workflow.Nodes.Where(node => node.Type == "Import").Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var archiveJobs = nodes
            .SelectMany(node => _archives.Where(job => job.NodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)).Select(job => (Node: node, Job: job)))
            .ToList();
        foreach (var node in nodes)
            if (archiveJobs.All(value => !value.Node.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"节点“{node.Title}”还没有选择 ZIP 文件。");

        var completedArchives = 0;
        foreach (var item in archiveJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = workflow.Connections.First(value =>
                value.FromNodeId.Equals(item.Node.Id, StringComparison.OrdinalIgnoreCase)
                && value.FromPort.Equals("batch", StringComparison.OrdinalIgnoreCase)
                && importIds.Contains(value.ToNodeId));
            try
            {
                item.Job.Status = "正在解压";
                Emit("archivesChanged", _archives);
                var entryProgress = new Progress<(int Completed, int Total)>(value =>
                {
                    Emit("workProgress", new
                    {
                        completed = completedArchives,
                        total = archiveJobs.Count,
                        stage = "preprocess",
                        archive = item.Job,
                        entryCompleted = value.Completed,
                        entryTotal = value.Total
                    });
                    Emit("archivesChanged", _archives);
                });
                passwords.TryGetValue(item.Node.Id, out var password);
                await _archiveService.ExtractAsync(item.Job, item.Node.Data.ArchiveEncoding, password, entryProgress, cancellationToken).ConfigureAwait(false);
                SynchronizeArchiveImages(item.Job, item.Node, connection);
            }
            catch (OperationCanceledException)
            {
                item.Job.Status = "已取消";
                throw;
            }
            catch (Exception exception)
            {
                item.Job.Status = "失败 · " + FriendlyMessage(exception);
                Emit("archivesChanged", _archives);
                throw new InvalidOperationException($"ZIP“{item.Job.Name}”解压失败：{FriendlyMessage(exception)}", exception);
            }
            completedArchives++;
            Emit("workProgress", new { completed = completedArchives, total = archiveJobs.Count, stage = "preprocess", archive = item.Job });
            Emit("archivesChanged", _archives);
        }
        Emit("jobsChanged", _jobs);
    }

    private void SynchronizeArchiveImages(ArchiveJob archive, WorkflowNode node, WorkflowConnection connection)
    {
        var imageEntries = archive.Entries.Where(entry => entry.IsImage).ToList();
        var validPaths = imageEntries.Select(entry => entry.EntryPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _jobs.Where(job => job.ArchiveJobId == archive.Id && !validPaths.Contains(job.ArchiveEntryPath)).ToList())
        {
            RemoveCache(stale.Id);
            _jobs.Remove(stale);
        }

        foreach (var entry in imageEntries)
        {
            var existing = _jobs.FirstOrDefault(job => job.ArchiveJobId == archive.Id && job.ArchiveEntryPath.Equals(entry.EntryPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && existing.SourcePath.Equals(entry.ExtractedPath, StringComparison.OrdinalIgnoreCase) && File.Exists(existing.SourcePath))
                continue;
            if (existing is not null)
            {
                RemoveCache(existing.Id);
                _jobs.Remove(existing);
            }
            try
            {
                var info = new FileInfo(entry.ExtractedPath);
                var dimensions = _imageEngine.ReadDimensions(entry.ExtractedPath);
                _jobs.Add(new FileJob
                {
                    SourcePath = entry.ExtractedPath,
                    OriginalSourcePath = entry.ExtractedPath,
                    Format = ImageMetadataReader.FormatName(entry.ExtractedPath),
                    OriginalSize = info.Length,
                    OriginalWidth = dimensions.Width,
                    OriginalHeight = dimensions.Height,
                    CurrentSize = info.Length,
                    CurrentWidth = dimensions.Width,
                    CurrentHeight = dimensions.Height,
                    TargetFormat = ImageMetadataReader.FormatName(entry.ExtractedPath),
                    TargetWidth = dimensions.Width,
                    TargetHeight = dimensions.Height,
                    ArchiveJobId = archive.Id,
                    ArchiveEntryPath = entry.EntryPath,
                    OriginNodeId = node.Id,
                    OriginConnectionId = connection.Id
                });
            }
            catch { }
        }
    }

    private static List<WorkflowNode> ConnectedArchiveNodes(WorkflowDocument workflow)
    {
        var imports = workflow.Nodes.Where(node => node.Type == "Import").Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return workflow.Nodes
            .Where(node => node.Type == "ZipExtract" && workflow.Connections.Any(connection =>
                connection.FromNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)
                && connection.FromPort.Equals("batch", StringComparison.OrdinalIgnoreCase)
                && imports.Contains(connection.ToNodeId)))
            .ToList();
    }

    private object ConfirmReplacedSources(string mode, bool willReplaceAgain)
    {
        var affected = GetReplacedInputJobs(_jobs)
            .Where(job => string.IsNullOrWhiteSpace(job.ArchiveJobId)
                || _archives.FirstOrDefault(archive => archive.Id.Equals(job.ArchiveJobId, StringComparison.OrdinalIgnoreCase)) is { } archive
                && _archiveService.IsPrepared(archive))
            .ToList();
        if (affected.Count == 0)
            return new { proceed = true, prompted = false, count = 0, ids = Array.Empty<string>() };

        var unavailable = affected.Where(job => !File.Exists(job.SourcePath)).ToList();
        if (unavailable.Count > 0)
        {
            var unavailableNames = FormatConfirmationNames(unavailable);
            return new
            {
                proceed = false,
                prompted = true,
                count = affected.Count,
                unavailable = true,
                message = $"检测到 {unavailable.Count} 张替换后的新图片也已不存在，无法继续处理。\n\n{unavailableNames}",
                ids = unavailable.Select(job => job.Id).ToArray()
            };
        }

        var operation = mode.Equals("estimate", StringComparison.OrdinalIgnoreCase) ? "进行精确预估" : "运行工作流";
        var names = FormatConfirmationNames(affected);
        var replaceAgain = willReplaceAgain
            ? "\n\n本次处理完成后，新图片仍会被再次替换。"
            : "";
        return new
        {
            proceed = false,
            prompted = true,
            count = affected.Count,
            unavailable = false,
            message = $"检测到 {affected.Count} 张已勾选图片的原图片已在上一次“替换原文件”后删除。\n\n是否使用上次生成的新图片继续{operation}？\n\n{names}{replaceAgain}",
            ids = affected.Select(job => job.Id).ToArray()
        };
    }

    private object ConfirmReplacedArchives(WorkflowDocument workflow, string mode, bool willReplaceAgain)
    {
        var connectedNodeIds = ConnectedArchiveNodes(workflow)
            .Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affected = _archives
            .Where(archive => connectedNodeIds.Contains(archive.NodeId) && archive.SourceWasReplaced)
            .ToList();
        if (affected.Count == 0)
            return new { proceed = true, prompted = false, count = 0, ids = Array.Empty<string>() };

        var unavailable = affected.Where(archive => !File.Exists(archive.SourcePath)).ToList();
        if (unavailable.Count > 0)
        {
            return new
            {
                proceed = false,
                prompted = true,
                count = affected.Count,
                unavailable = true,
                message = $"检测到 {unavailable.Count} 个替换后的新 ZIP 已不存在，无法继续处理。\n\n{FormatArchiveConfirmationNames(unavailable)}",
                ids = unavailable.Select(archive => archive.Id).ToArray()
            };
        }

        var operation = mode.Equals("estimate", StringComparison.OrdinalIgnoreCase) ? "进行精确预估" : "运行工作流";
        var replaceAgain = willReplaceAgain
            ? "\n\n本次处理完成后，新 ZIP 仍会被再次替换。"
            : "";
        return new
        {
            proceed = false,
            prompted = true,
            count = affected.Count,
            unavailable = false,
            message = $"检测到 {affected.Count} 个原 ZIP 已在上一次工作流中被替换。\n\n是否将上次生成的新 ZIP 作为新的输入，重新解压后继续{operation}？再次压缩 JPEG 可能继续降低画质。\n\n{FormatArchiveConfirmationNames(affected)}{replaceAgain}",
            ids = affected.Select(archive => archive.Id).ToArray()
        };
    }

    private object AcceptReplacedArchives(IReadOnlyCollection<string> ids)
    {
        var selectedIds = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affected = _archives
            .Where(archive => selectedIds.Contains(archive.Id) && archive.SourceWasReplaced)
            .ToList();
        if (affected.Count != selectedIds.Count)
            throw new InvalidOperationException("待更新的 ZIP 列表已经发生变化，请重新运行工作流。");

        foreach (var archive in affected)
        {
            if (!File.Exists(archive.SourcePath))
                throw new FileNotFoundException("替换后的新 ZIP 不存在。", archive.SourcePath);
            foreach (var job in _jobs.Where(job => job.ArchiveJobId.Equals(archive.Id, StringComparison.OrdinalIgnoreCase)).ToList())
                RemoveCache(job.Id);
            _jobs.RemoveAll(job => job.ArchiveJobId.Equals(archive.Id, StringComparison.OrdinalIgnoreCase));
            AcceptReplacementAsNewArchiveOriginal(archive);
        }

        Emit("archivesChanged", _archives);
        Emit("jobsChanged", _jobs);
        return new { archives = _archives, jobs = _jobs };
    }

    private IReadOnlyList<FileJob> AcceptReplacedSources(IReadOnlyCollection<string> ids)
    {
        var selectedIds = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affected = GetReplacedInputJobs(_jobs)
            .Where(job => selectedIds.Contains(job.Id))
            .ToList();
        if (affected.Count != selectedIds.Count)
            throw new InvalidOperationException("待更新的图片列表已经发生变化，请重新运行工作流。");

        foreach (var job in affected) AcceptReplacementAsNewOriginal(job);
        Emit("jobsChanged", _jobs);
        return _jobs;
    }

    internal static IReadOnlyList<FileJob> GetReplacedInputJobs(IEnumerable<FileJob> jobs)
        => jobs.Where(job => job.Checked && job.SourceWasReplaced).ToList();

    internal static void AcceptReplacementAsNewOriginal(FileJob job)
    {
        if (!File.Exists(job.SourcePath))
            throw new FileNotFoundException("替换后的新图片不存在。", job.SourcePath);

        var info = new FileInfo(job.SourcePath);
        job.OriginalSourcePath = job.SourcePath;
        job.Format = ImageMetadataReader.FormatName(job.SourcePath);
        job.OriginalSize = info.Length;
        job.OriginalWidth = job.CurrentWidth > 0 ? job.CurrentWidth : job.TargetWidth;
        job.OriginalHeight = job.CurrentHeight > 0 ? job.CurrentHeight : job.TargetHeight;
        job.CurrentSize = info.Length;
        job.SourceWasReplaced = false;
    }

    private static string FormatConfirmationNames(IReadOnlyList<FileJob> jobs)
    {
        var names = jobs.Take(5)
            .Select(job => Path.GetFileName(string.IsNullOrWhiteSpace(job.OriginalSourcePath) ? job.SourcePath : job.OriginalSourcePath));
        var value = "涉及文件：" + string.Join("、", names);
        return jobs.Count > 5 ? value + $" 等 {jobs.Count} 张" : value;
    }

    private static string FormatArchiveConfirmationNames(IReadOnlyList<ArchiveJob> archives)
    {
        var names = archives.Take(5).Select(archive => Path.GetFileName(archive.SourcePath));
        var value = "涉及 ZIP：" + string.Join("、", names);
        return archives.Count > 5 ? value + $" 等 {archives.Count} 个" : value;
    }

    internal static void AcceptReplacementAsNewArchiveOriginal(ArchiveJob archive)
    {
        var info = new FileInfo(archive.SourcePath);
        archive.Size = info.Length;
        archive.SourceWasReplaced = false;
        archive.PreparedFingerprint = "";
        archive.OutputDirectory = "";
        archive.Entries.Clear();
        archive.OwnsOutputDirectory = false;
        archive.EntryCount = 0;
        archive.ImageCount = 0;
        archive.Progress = 0;
        archive.Status = "待重新预处理";
    }

    private async Task<WorkSummary> StartWorkAsync(
        WorkflowDocument workflow,
        bool estimate,
        bool useReplacedSources,
        bool preprocessArchives,
        IReadOnlyDictionary<string, string> archivePasswords)
    {
        if (_workCancellation is not null) throw new InvalidOperationException("已有任务正在运行。");
        ValidateWorkflow(workflow);
        _workCancellation = new CancellationTokenSource();
        var cancellationToken = _workCancellation.Token;
        var summary = new WorkSummary();
        var completed = 0;
        var successes = 0;
        var failures = 0;
        var cacheHits = 0;
        var replaced = 0;
        var skipped = 0;
        try
        {
            var archiveNodes = ConnectedArchiveNodes(workflow);
            if (archiveNodes.Count > 0)
            {
                var archiveNodeIds = archiveNodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var selectedArchives = _archives.Where(job => archiveNodeIds.Contains(job.NodeId)).ToList();
                foreach (var node in archiveNodes)
                    if (selectedArchives.All(job => !job.NodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"节点“{node.Title}”还没有选择 ZIP 文件。");

                var needsPreprocessing = selectedArchives.Any(job => !_archiveService.IsPrepared(job));
                if (needsPreprocessing && !preprocessArchives)
                    throw new InvalidOperationException("精确预估前需要先完成 ZIP 解压预处理。");
                if (needsPreprocessing)
                {
                    Emit("workState", new { busy = true, mode = estimate ? "estimate" : "run", stage = "preprocess", total = selectedArchives.Count });
                    await PrepareArchiveNodesAsync(workflow, archiveNodes, archivePasswords, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    foreach (var node in archiveNodes)
                    {
                        var connection = workflow.Connections.First(value => value.FromNodeId == node.Id && value.FromPort == "batch");
                        foreach (var archive in selectedArchives.Where(job => job.NodeId == node.Id))
                            SynchronizeArchiveImages(archive, node, connection);
                    }
                }
            }

            var selected = _jobs.Where(job => job.Checked).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("请至少勾选一张图片，或先连接并选择 ZIP 文件。");
            if (selected.Any(job => job.SourceWasReplaced) && !useReplacedSources)
                throw new InvalidOperationException("原图片已被上一次工作流删除。请确认使用新图片后再继续。");
            summary.Total = selected.Count;
            _imageEngine.ValidateDependencies();
            _imageEngine.ConfigureConcurrency(Math.Clamp(workflow.Parallelism, 1, 16));
            foreach (var item in _jobs)
            {
                item.RouteNodeIds.Clear();
                item.RouteConnectionIds.Clear();
                item.TargetSizeNotes.Clear();
                item.OutputNodeId = "";
                item.OutputReady = false;
                item.OutputWarning = null;
                if (item.Checked)
                {
                    item.OutputPath = null;
                    item.EstimatedSize = null;
                    item.Status = estimate ? "待预估" : "待处理";
                }
            }
            Emit("jobsChanged", _jobs);
            Emit("workState", new { busy = true, mode = estimate ? "estimate" : "run", stage = "images", total = selected.Count });

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(workflow.Parallelism, 1, 16),
                CancellationToken = cancellationToken
            };
            await Parallel.ForEachAsync(selected, options, async (job, token) =>
            {
                try
                {
                    job.Status = estimate ? "正在精确预估" : "正在处理";
                    EmitJob(job);
                    var signature = BuildSignature(job, workflow);
                    ExecutionResult result;
                    EstimateCacheEntry? cache = null;
                    var cacheHit = workflow.CacheEstimates && TryGetCache(job.Id, signature, out cache);
                    if (cacheHit && cache is not null)
                    {
                        result = cache.RestoreResult(job.SourcePath);
                        Interlocked.Increment(ref cacheHits);
                    }
                    else
                    {
                        result = await _runner.ExecuteAsync(job, workflow, token);
                    }

                    job.ApplyExecutionResult(result);
                    token.ThrowIfCancellationRequested();

                    if (estimate)
                    {
                        if (!result.Transformed)
                        {
                            job.OutputPath = null;
                            job.Status = "不处理";
                            RemoveCache(job.Id);
                            Interlocked.Increment(ref skipped);
                        }
                        else if (cacheHit)
                        {
                            job.Status = "预估完成 · 命中缓存";
                        }
                        else
                        {
                            job.Status = "预估完成 · 已缓存";
                        }
                        if (result.Transformed && workflow.CacheEstimates && !cacheHit)
                            StoreCache(job.Id, signature, result);
                        else if (!cacheHit)
                            CleanupExecution(result, null);
                    }
                    else
                    {
                        if (!result.Transformed)
                        {
                            job.OutputPath = null;
                            job.Status = "不处理";
                            job.OutputReady = true;
                            Interlocked.Increment(ref skipped);
                            RemoveCache(job.Id);
                        }
                        else
                        {
                            var outputNode = workflow.Nodes.First(node => node.Id == result.OutputNodeId);
                            var persisted = PersistOutput(job, result, outputNode);
                            job.ApplySavedOutput(result, persisted, cacheHit);
                            if (persisted.Replaced)
                            {
                                Interlocked.Increment(ref replaced);
                                RemoveCache(job.Id);
                            }
                            if (!cacheHit) CleanupExecution(result, null);
                        }
                    }
                    if (result.TargetSizeNotes.Count > 0) job.Status += " · 已保留最小结果";
                    Interlocked.Increment(ref successes);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { job.Status = "已取消"; }
                catch (Exception exception)
                {
                    job.Status = "失败 · " + FriendlyMessage(exception);
                    Interlocked.Increment(ref failures);
                }
                finally
                {
                    EmitJob(job);
                    var done = Interlocked.Increment(ref completed);
                    Emit("workProgress", new { completed = done, total = selected.Count, mode = estimate ? "estimate" : "run", stage = "images", job });
                }
            });
            summary.Successes = successes;
            summary.Failures = failures;
            summary.CacheHits = cacheHits;
            summary.Replaced = replaced;
            summary.Skipped = skipped;
            if (!estimate)
            {
                var archiveResult = await PackageArchivesAsync(workflow, cancellationToken).ConfigureAwait(false);
                summary.PackedArchives = archiveResult.Packed;
                summary.ReplacedArchives = archiveResult.Replaced;
                summary.ArchiveFailures = archiveResult.Failures;
                summary.CleanedExtractionFolders = archiveResult.Cleaned;
            }
            return summary;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            summary.Successes = successes;
            summary.Failures = failures;
            summary.CacheHits = cacheHits;
            summary.Replaced = replaced;
            summary.Skipped = skipped;
            summary.Cancelled = true;
            return summary;
        }
        finally
        {
            summary.Successes = successes;
            summary.Failures = failures;
            summary.CacheHits = cacheHits;
            summary.Replaced = replaced;
            summary.Skipped = skipped;
            _workCancellation.Dispose();
            _workCancellation = null;
            Emit("workState", new { busy = false, mode = estimate ? "estimate" : "run", stage = "complete", summary });
        }
    }

    private async Task<(int Packed, int Replaced, int Failures, int Cleaned)> PackageArchivesAsync(
        WorkflowDocument workflow,
        CancellationToken cancellationToken)
    {
        var packageNodes = workflow.Nodes.Where(node => node.Type == "ZipPack").ToList();
        var packagePlans = packageNodes
            .Select(node => new
            {
                Node = node,
                Inputs = workflow.Connections.Where(connection =>
                    connection.ToNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)
                    && connection.ToPort.Equals("in", StringComparison.OrdinalIgnoreCase)
                    && workflow.Nodes.Any(source => source.Id == connection.FromNodeId && source.Type == "Output"))
                    .ToList()
            })
            .Where(plan => plan.Inputs.Count > 0)
            .ToList();
        if (packagePlans.Count == 0) return (0, 0, 0, 0);

        var total = packagePlans.Sum(plan =>
        {
            var outputIds = plan.Inputs.Select(connection => connection.FromNodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _archives.Count(archive => _jobs.Any(job =>
                job.ArchiveJobId == archive.Id && outputIds.Contains(job.OutputNodeId)));
        });
        var completed = 0;
        var packed = 0;
        var replaced = 0;
        var failures = 0;
        var cleaned = 0;
        var failedArchiveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleanupTargets = new Dictionary<string, (ArchiveJob Archive, WorkflowConnection Connection, WorkflowNode Node)>(StringComparer.OrdinalIgnoreCase);
        Emit("workState", new { busy = true, mode = "run", stage = "postprocess", total });

        foreach (var plan in packagePlans)
        {
            var inputByOutput = plan.Inputs.ToDictionary(connection => connection.FromNodeId, StringComparer.OrdinalIgnoreCase);
            var cleanupConnection = workflow.Connections.FirstOrDefault(connection =>
                connection.FromNodeId.Equals(plan.Node.Id, StringComparison.OrdinalIgnoreCase)
                && connection.FromPort.Equals("batch", StringComparison.OrdinalIgnoreCase)
                && workflow.Nodes.Any(target => target.Id == connection.ToNodeId && target.Type == "DeleteExtracted"));
            var cleanupNode = cleanupConnection is null
                ? null
                : workflow.Nodes.First(node => node.Id.Equals(cleanupConnection.ToNodeId, StringComparison.OrdinalIgnoreCase));
            foreach (var archive in _archives.Where(value => _jobs.Any(job =>
                         job.ArchiveJobId == value.Id && inputByOutput.ContainsKey(job.OutputNodeId))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var archiveJobs = _jobs.Where(job => job.ArchiveJobId == archive.Id).ToList();
                var sourceDirectory = Path.GetDirectoryName(archive.SourcePath) ?? Environment.CurrentDirectory;
                var baseName = Path.GetFileNameWithoutExtension(archive.SourcePath);
                var temporary = Path.Combine(sourceDirectory, $".{baseName}.aichan-pack-{Guid.NewGuid():N}.zip");
                string? finalPath = null;
                try
                {
                    var replacements = BuildArchiveReplacements(archiveJobs, inputByOutput.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
                    archive.Status = "正在 Store 打包";
                    archive.Progress = 0;
                    Emit("archivesChanged", _archives);
                    var expectedEntries = archive.Entries.Count(entry => entry.IsImage || plan.Node.Data.PreserveNonImageFiles);
                    var entryProgress = new Progress<(int Completed, int Total)>(value =>
                    {
                        archive.Progress = value.Total == 0 ? 100 : (int)Math.Round(value.Completed * 100d / value.Total);
                        Emit("archivesChanged", _archives);
                    });
                    await _archiveService.PackStoreAsync(
                        archive,
                        replacements,
                        plan.Node.Data.PreserveNonImageFiles,
                        temporary,
                        entryProgress,
                        cancellationToken).ConfigureAwait(false);
                    archive.Status = "正在校验 ZIP";
                    Emit("archivesChanged", _archives);
                    await ArchiveService.VerifyAsync(temporary, expectedEntries, cancellationToken).ConfigureAwait(false);

                    if (plan.Node.Data.ReplaceSourceArchive)
                    {
                        ReplaceArchiveFile(archive.SourcePath, temporary);
                        finalPath = archive.SourcePath;
                        replaced++;
                        archive.Status = "打包完成 · 已替换原 ZIP";
                        archive.Size = new FileInfo(archive.SourcePath).Length;
                        archive.PreparedFingerprint = "";
                        archive.SourceWasReplaced = true;
                    }
                    else
                    {
                        finalPath = ReserveOutput(sourceDirectory, baseName + "_processed", ".zip");
                        File.Move(temporary, finalPath);
                        archive.Status = "打包完成 · " + Path.GetFileName(finalPath);
                    }
                    archive.Progress = 100;
                    _lastOutputDirectory = sourceDirectory;
                    packed++;
                    if (cleanupConnection is not null && cleanupNode is not null)
                        cleanupTargets[archive.Id] = (archive, cleanupConnection, cleanupNode);

                    foreach (var job in archiveJobs.Where(job => inputByOutput.TryGetValue(job.OutputNodeId, out _)))
                    {
                        var connection = inputByOutput[job.OutputNodeId];
                        if (!job.RouteConnectionIds.Contains(connection.Id)) job.RouteConnectionIds.Add(connection.Id);
                        if (!job.RouteNodeIds.Contains(plan.Node.Id)) job.RouteNodeIds.Add(plan.Node.Id);
                        EmitJob(job);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    archive.Status = "已取消";
                    TryDelete(temporary);
                    throw;
                }
                catch (Exception exception)
                {
                    TryDelete(temporary);
                    archive.Status = "打包失败 · " + FriendlyMessage(exception);
                    failedArchiveIds.Add(archive.Id);
                    failures++;
                }
                finally
                {
                    completed++;
                    Emit("archivesChanged", _archives);
                    Emit("workProgress", new { completed, total, stage = "postprocess", archive, outputPath = finalPath });
                }
            }
        }

        var pendingCleanup = cleanupTargets.Values
            .Where(target => !failedArchiveIds.Contains(target.Archive.Id))
            .ToList();
        if (pendingCleanup.Count > 0)
        {
            Emit("workState", new { busy = true, mode = "run", stage = "cleanup", total = pendingCleanup.Count });
            var cleanupCompleted = 0;
            foreach (var target in pendingCleanup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    target.Archive.Status = "正在删除解压文件夹";
                    Emit("archivesChanged", _archives);
                    _archiveService.DeleteExtractionDirectory(target.Archive);
                    target.Archive.Status = "后处理完成 · 已删除解压文件夹";
                    cleaned++;

                    foreach (var job in _jobs.Where(job =>
                                 job.ArchiveJobId.Equals(target.Archive.Id, StringComparison.OrdinalIgnoreCase)
                                 && job.RouteNodeIds.Contains(target.Connection.FromNodeId, StringComparer.OrdinalIgnoreCase)))
                    {
                        if (!job.RouteConnectionIds.Contains(target.Connection.Id)) job.RouteConnectionIds.Add(target.Connection.Id);
                        if (!job.RouteNodeIds.Contains(target.Node.Id)) job.RouteNodeIds.Add(target.Node.Id);
                        EmitJob(job);
                    }
                }
                catch (Exception exception)
                {
                    target.Archive.Status = "ZIP 已生成 · 删除解压目录失败：" + FriendlyMessage(exception);
                    failures++;
                }
                finally
                {
                    cleanupCompleted++;
                    Emit("archivesChanged", _archives);
                    Emit("workProgress", new { completed = cleanupCompleted, total = pendingCleanup.Count, stage = "cleanup", archive = target.Archive });
                }
            }
        }
        return (packed, replaced, failures, cleaned);
    }

    private static void ReplaceArchiveFile(string sourcePath, string replacementPath)
    {
        WaitForExclusiveAccess(sourcePath);
        var directory = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
        var backup = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(sourcePath)}.aichan-backup-{Guid.NewGuid():N}.zip");
        File.Move(sourcePath, backup);
        try
        {
            File.Move(replacementPath, sourcePath);
        }
        catch
        {
            if (!File.Exists(sourcePath) && File.Exists(backup)) File.Move(backup, sourcePath);
            throw;
        }

        try
        {
            ShellRecycleBin.DeleteFile(backup);
        }
        catch { }
    }

    internal static Dictionary<string, string> BuildArchiveReplacements(IReadOnlyList<FileJob> jobs, IReadOnlySet<string> outputNodeIds)
    {
        var incomplete = jobs.FirstOrDefault(job => job.Checked && !job.OutputReady);
        if (incomplete is not null)
            throw new InvalidOperationException($"图片“{incomplete.Name}”尚未完成输出（{incomplete.Status}），已阻止打包，不会回退使用原图。");

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs.Where(job => outputNodeIds.Contains(job.OutputNodeId)))
        {
            if (!job.OutputReady)
                throw new InvalidOperationException($"图片“{job.Name}”的输出未就绪，已阻止打包。");
            var source = string.IsNullOrWhiteSpace(job.OutputPath) ? job.SourcePath : job.OutputPath;
            if (!File.Exists(source))
                throw new FileNotFoundException($"图片“{job.Name}”的输出文件丢失，已阻止打包，不会回退使用原图。", source);
            replacements[job.ArchiveEntryPath] = source;
        }
        return replacements;
    }

    private ImageOutputResult PersistOutput(FileJob job, ExecutionResult result, WorkflowNode outputNode)
    {
        var saved = _outputWriter.Write(job, result, outputNode);
        _lastOutputDirectory = Path.GetDirectoryName(saved.Path);
        return saved;
    }

    private string ReserveOutput(string directory, string baseName, string extension)
        => _outputWriter.Reserve(directory, baseName, extension);

    internal static void WaitForExclusiveAccess(string path)
    {
        IOException? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
            }
            catch (IOException exception)
            {
                lastError = exception;
                if (attempt < 19) Thread.Sleep(100);
            }
        }

        throw new IOException($"原文件仍被其他程序占用：{Path.GetFileName(path)}。请关闭图片预览或资源管理器预览窗格后重试。", lastError);
    }

    private void OpenOutputDirectory()
    {
        var directory = _lastOutputDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            directory = _jobs.Select(job => job.OutputPath).Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetDirectoryName).LastOrDefault();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new InvalidOperationException("还没有可打开的输出目录。");
        Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
    }

    internal static void ValidateWorkflow(WorkflowDocument workflow)
    {
        if (!workflow.Nodes.Any(node => node.Type == "Import"))
            throw new InvalidOperationException("工作流缺少导入节点。");
        if (!workflow.Nodes.Any(node => node.Type == "Output"))
            throw new InvalidOperationException("工作流缺少保存输出节点。");
        if (workflow.Connections.Count == 0)
            throw new InvalidOperationException("工作流还没有连接线。");
        foreach (var output in workflow.Nodes.Where(node => node.Type == "Output" && !node.Data.SameFolder))
            if (string.IsNullOrWhiteSpace(output.Data.OutputDirectory))
                throw new InvalidOperationException($"节点“{output.Title}”没有设置输出目录。");
        foreach (var extract in workflow.Nodes.Where(node => node.Type == "ZipExtract"))
        {
            var connection = workflow.Connections.FirstOrDefault(value => value.FromNodeId == extract.Id && value.FromPort == "batch");
            if (connection is not null && workflow.Nodes.All(node => node.Id != connection.ToNodeId || node.Type != "Import"))
                throw new InvalidOperationException($"节点“{extract.Title}”只能连接到导入节点。");
        }
        foreach (var package in workflow.Nodes.Where(node => node.Type == "ZipPack"))
        {
            var inputs = workflow.Connections.Where(value => value.ToNodeId == package.Id).ToList();
            if (inputs.Count > 0 && inputs.Any(input => workflow.Nodes.All(node => node.Id != input.FromNodeId || node.Type != "Output")))
                throw new InvalidOperationException($"节点“{package.Title}”只能接在保存输出节点之后。");
            var outputs = workflow.Connections.Where(value => value.FromNodeId == package.Id).ToList();
            if (outputs.Any(output => workflow.Nodes.All(node => node.Id != output.ToNodeId || node.Type != "DeleteExtracted")))
                throw new InvalidOperationException($"节点“{package.Title}”的批次出口只能连接删除解压目录节点。");
        }
        foreach (var cleanup in workflow.Nodes.Where(node => node.Type == "DeleteExtracted"))
        {
            var inputs = workflow.Connections.Where(value => value.ToNodeId == cleanup.Id).ToList();
            if (inputs.Count > 0 && inputs.Any(input => workflow.Nodes.All(node => node.Id != input.FromNodeId || node.Type != "ZipPack")))
                throw new InvalidOperationException($"节点“{cleanup.Title}”只能接在 ZIP 压缩节点之后。");
        }
        ValidateJpegOutputPaths(workflow);
    }

    [Flags]
    private enum ImageFormatSet
    {
        None = 0,
        Jpg = 1,
        Png = 2,
        Webp = 4,
        Other = 8,
        All = Jpg | Png | Webp | Other
    }

    private static void ValidateJpegOutputPaths(WorkflowDocument workflow)
    {
        var nodes = workflow.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var import = workflow.Nodes.First(node => node.Type == "Import");
        var outgoing = workflow.Connections
            .GroupBy(connection => connection.FromNodeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(WorkflowNode Node, ImageFormatSet Formats, bool Transformed)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reachedOutput = false;
        queue.Enqueue((import, ImageFormatSet.All, false));

        while (queue.Count > 0)
        {
            var (node, formats, transformed) = queue.Dequeue();
            if (!visited.Add(node.Id + ":" + (int)formats + ":" + transformed)) continue;
            if (node.Type == "Output")
            {
                reachedOutput = true;
                var unsupported = formats & ~ImageFormatSet.Jpg;
                if (transformed && unsupported != ImageFormatSet.None)
                    throw new InvalidOperationException(
                        $"保存输出节点“{node.Title}”存在经过处理但未生成 JPG 的可达路径（{FormatSetLabel(unsupported)}）。" +
                        "请在该分支接入“转为 JPG”“JPG 画质压缩”或“目标体积压缩”节点。");
                continue;
            }

            outgoing.TryGetValue(node.Id, out var connections);
            if (node.Type == "TargetSize" && node.Data.TargetKeepSmallestOnUnmet
                && (connections is null || !connections.Any(connection =>
                    connection.FromPort.Equals("unmet", StringComparison.OrdinalIgnoreCase)
                    && nodes.ContainsKey(connection.ToNodeId))))
                throw new InvalidOperationException(
                    $"节点“{node.Title}”已勾选“未达标时输出最小结果”，但“未达标”出口缺失或未连接有效节点。请连接该出口后再启动工作流。");
            if (connections is null) continue;
            if (node.Type == "FormatFilter")
            {
                Enqueue("jpg", formats & ImageFormatSet.Jpg, transformed);
                Enqueue("png", formats & ImageFormatSet.Png, transformed);
                Enqueue("webp", formats & ImageFormatSet.Webp, transformed);
                Enqueue("other", formats & ImageFormatSet.Other, transformed);
                continue;
            }

            if (node.Type == "TargetSize")
            {
                Enqueue("out", ImageFormatSet.Jpg, true);
                if (node.Data.TargetKeepSmallestOnUnmet)
                    Enqueue("unmet", ImageFormatSet.Jpg, true);
                else
                    Enqueue("unmet", formats, transformed);
                continue;
            }

            var nextFormats = node.Type is "ConvertJpg" or "Quality"
                ? ImageFormatSet.Jpg
                : formats;
            var nextTransformed = transformed || node.Type switch
            {
                "ConvertJpg" or "Quality" or "Descreen" => true,
                "Resize" => Math.Clamp(node.Data.ScalePercent, 20, 100) < 100,
                _ => false
            };
            if (node.Type is "SizeFilter" or "ResolutionFilter")
            {
                Enqueue("match", nextFormats, nextTransformed);
                Enqueue("else", nextFormats, nextTransformed);
            }
            else
            {
                Enqueue("out", nextFormats, nextTransformed);
            }

            void Enqueue(string port, ImageFormatSet routedFormats, bool routedTransformed)
            {
                if (routedFormats == ImageFormatSet.None) return;
                foreach (var connection in connections.Where(value => value.FromPort.Equals(port, StringComparison.OrdinalIgnoreCase)))
                    if (nodes.TryGetValue(connection.ToNodeId, out var target)) queue.Enqueue((target, routedFormats, routedTransformed));
            }
        }

        if (!reachedOutput)
            throw new InvalidOperationException("从导入节点出发的图片路径无法到达保存输出节点。");
    }

    private static string FormatSetLabel(ImageFormatSet formats)
    {
        var labels = new List<string>();
        if (formats.HasFlag(ImageFormatSet.Png)) labels.Add("PNG");
        if (formats.HasFlag(ImageFormatSet.Webp)) labels.Add("WebP");
        if (formats.HasFlag(ImageFormatSet.Other)) labels.Add("其他格式");
        return string.Join("/", labels);
    }

    internal static string BuildSignature(FileJob job, WorkflowDocument workflow)
    {
        var info = new FileInfo(job.SourcePath);
        var builder = new StringBuilder()
            .Append(job.SourcePath).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks)
            .Append('|').Append(workflow.AutoGrayscale);
        foreach (var node in workflow.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            builder.Append('|').Append(node.Id).Append(':').Append(node.Type).Append(':')
                .Append(node.Data.SizeOperator).Append(':').Append(node.Data.SizeMb).Append(':')
                .Append(node.Data.ScalePercent).Append(':').Append(node.Data.QualityPercent).Append(':')
                .Append(node.Data.TargetSizeMb).Append(':').Append(node.Data.TargetStartQuality).Append(':')
                .Append(node.Data.TargetQualitySpan).Append(':').Append(node.Data.TargetMinimumQuality).Append(':')
                .Append(node.Data.TargetKeepSmallestOnUnmet).Append(':')
                .Append(node.Data.DescreenLevel).Append(':')
                .Append(node.Data.WidthEnabled).Append(':').Append(node.Data.WidthOperator).Append(':').Append(node.Data.WidthValue).Append(':')
                .Append(node.Data.HeightEnabled).Append(':').Append(node.Data.HeightOperator).Append(':').Append(node.Data.HeightValue).Append(':')
                .Append(node.Data.ResolutionJoin).Append(':').Append(node.Data.SameFolder).Append(':')
                .Append(node.Data.OutputDirectory).Append(':').Append(node.Data.ReplaceOriginal).Append(':')
                .Append(node.Data.ArchiveEncoding).Append(':').Append(node.Data.PreserveNonImageFiles).Append(':')
                .Append(node.Data.ReplaceSourceArchive);
        }
        foreach (var connection in workflow.Connections.OrderBy(value => value.FromNodeId + value.FromPort, StringComparer.Ordinal))
            builder.Append('|').Append(connection.Id).Append(':').Append(connection.FromNodeId).Append(':').Append(connection.FromPort).Append('>')
                .Append(connection.ToNodeId).Append(':').Append(connection.ToPort);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private bool TryGetCache(string jobId, string signature, out EstimateCacheEntry? cache)
    {
        if (_cache.TryGetValue(jobId, out var value) && value.Signature == signature && File.Exists(value.ResultPath))
        {
            cache = value;
            return true;
        }
        cache = null;
        return false;
    }

    private void StoreCache(string jobId, string signature, ExecutionResult result)
    {
        RemoveCache(jobId);
        _cache[jobId] = EstimateCacheEntry.FromResult(signature, result);
        CleanupExecution(result, result.FinalPath);
    }

    private void RemoveCache(string jobId)
    {
        if (!_cache.TryRemove(jobId, out var cache)) return;
        if (IsTemporary(cache.ResultPath)) TryDelete(cache.ResultPath);
    }

    private void CleanupExecution(ExecutionResult result, string? keep)
    {
        foreach (var path in result.TemporaryFiles.Append(result.FinalPath).Distinct(StringComparer.OrdinalIgnoreCase))
            if (IsTemporary(path) && !path.Equals(keep, StringComparison.OrdinalIgnoreCase)) TryDelete(path);
    }

    private bool IsTemporary(string path)
        => path.StartsWith(_cacheRoot, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void EmitJob(FileJob job) => Emit("jobUpdated", job);

    public void NotifyWindowStateChanged()
        => Emit("windowStateChanged", new { maximized = _owner.WindowState == WindowState.Maximized });

    private void Emit(string eventName, object? data)
    {
        _owner.Dispatcher.BeginInvoke(() =>
        {
            if (_browser.CoreWebView2 is null) return;
            var json = JsonSerializer.Serialize(new HostEvent { EventName = eventName, Data = data }, _json);
            _browser.CoreWebView2.PostWebMessageAsJson(json);
        });
    }

    private void Reply(HostResponse response)
    {
        if (_browser.CoreWebView2 is null) return;
        _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(response, _json));
    }

    private WorkflowDocument ReadWorkflow(JsonElement payload)
        => payload.Deserialize<WorkflowDocument>(_json) ?? throw new InvalidOperationException("工作流数据无效。");

    private WorkflowDocument ReadWorkflowProperty(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value)
            ? value.Deserialize<WorkflowDocument>(_json) ?? throw new InvalidOperationException("工作流数据无效。")
            : throw new InvalidOperationException("缺少工作流数据。");

    private WorkRequestData ReadWorkRequest(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("workflow", out var workflowValue))
        {
            var workflow = workflowValue.Deserialize<WorkflowDocument>(_json)
                ?? throw new InvalidOperationException("工作流数据无效。");
            var useReplacedSources = payload.TryGetProperty("useReplacedSources", out var confirmation)
                && confirmation.ValueKind is JsonValueKind.True or JsonValueKind.False
                && confirmation.GetBoolean();
            var preprocessArchives = payload.TryGetProperty("preprocessArchives", out var preprocessing)
                && preprocessing.ValueKind is JsonValueKind.True or JsonValueKind.False
                && preprocessing.GetBoolean();
            var archivePasswords = payload.TryGetProperty("archivePasswords", out var passwords)
                && passwords.ValueKind == JsonValueKind.Object
                ? passwords.Deserialize<Dictionary<string, string>>(_json) ?? new Dictionary<string, string>()
                : new Dictionary<string, string>();
            return new WorkRequestData(workflow, useReplacedSources, preprocessArchives, archivePasswords);
        }

        return new WorkRequestData(ReadWorkflow(payload), false, false, new Dictionary<string, string>());
    }

    private sealed record WorkRequestData(
        WorkflowDocument Workflow,
        bool UseReplacedSources,
        bool PreprocessArchives,
        IReadOnlyDictionary<string, string> ArchivePasswords);

    private static string ReadString(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    private static string? ReadOptionalString(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static void CaptureFrontendStartupMetrics(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("metrics", out var metrics) ||
            metrics.ValueKind != JsonValueKind.Object)
            return;

        foreach (var metric in metrics.EnumerateObject())
        {
            if (metric.Name.Length is < 1 or > 64 ||
                metric.Name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
                continue;
            if (metric.Value.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0)
                StartupTelemetry.SetMetric($"frontend.{metric.Name}", value);
        }
    }
    private static bool ReadBoolean(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.GetBoolean();
    private static string[] ReadStringArray(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) ? value.Deserialize<string[]>() ?? Array.Empty<string>() : Array.Empty<string>();

    private static string FriendlyMessage(Exception exception)
    {
        var value = exception;
        while (value.InnerException is not null) value = value.InnerException;
        return string.IsNullOrWhiteSpace(value.Message) ? "操作失败。" : value.Message;
    }

    private static void CleanupStaleCacheSessions(string cacheParent, string currentRoot)
    {
        StartupTelemetry.Mark("cache.cleanup.start");
        if (!Directory.Exists(cacheParent)) return;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(cacheParent))
            {
                if (directory.Equals(currentRoot, StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(directory);
                var separator = name.IndexOf('_');
                if (separator <= 0 || !int.TryParse(name[..separator], out var processId)) continue;

                var running = false;
                try
                {
                    using var process = Process.GetProcessById(processId);
                    running = !process.HasExited;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }

                if (!running)
                    try { Directory.Delete(directory, true); } catch { }
            }
        }
        catch { }
        finally
        {
            StartupTelemetry.Mark("cache.cleanup.complete");
            StartupTelemetry.FlushInBackground("cache-cleanup-complete");
        }
    }

    public void Dispose()
    {
        _workCancellation?.Cancel();
        _workCancellation?.Dispose();
        try { if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, true); } catch { }
        try
        {
            if (Directory.Exists(_cacheParent) && !Directory.EnumerateFileSystemEntries(_cacheParent).Any())
                Directory.Delete(_cacheParent);
        }
        catch { }
    }
}
