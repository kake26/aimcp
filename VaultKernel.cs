using System.Collections;
using System.Diagnostics;

public sealed class VaultKernel : BackgroundService, IDisposable
{
    private readonly string _vaultPath;
    private readonly string? _fossilRepo;
    private readonly VaultIndex _index;
    private readonly ILogger<VaultKernel> _logger;
    private readonly TimeSpan _fossilBatchDelay;
    private readonly Channel<VaultCommandEnvelope> _queue = Channel.CreateUnbounded<VaultCommandEnvelope>();
    private readonly Channel<bool> _commitSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private int _pendingOperationCount;
    private readonly object _batchLock = new();

    public VaultKernel(IConfiguration config, VaultIndex index, ILogger<VaultKernel> logger)
    {
        _vaultPath = Environment.GetEnvironmentVariable("OBSIDIAN_VAULT_PATH")
                     ?? config["VaultPath"]
                     ?? string.Empty;
        _fossilRepo = Environment.GetEnvironmentVariable("AIMCP_FOSSIL_REPO") ?? config["Fossil:RepoPath"];
        var batchDelayMilliseconds = config.GetValue("Fossil:BatchDelayMilliseconds", 1000);
        _fossilBatchDelay = TimeSpan.FromMilliseconds(Math.Max(0, batchDelayMilliseconds));
        _index = index;
        _logger = logger;
    }

    public event Action<VaultEvent>? VaultChanged;

    public Task<VaultCommandResult> CreateNoteAsync(string path, string content, CancellationToken cancellationToken = default)
        => EnqueueAsync(new CreateNoteCommand(path, content), cancellationToken);

    public Task<VaultCommandResult> UpdateNoteAsync(string path, string content, CancellationToken cancellationToken = default)
        => EnqueueAsync(new UpdateNoteCommand(path, content), cancellationToken);

    public Task<VaultCommandResult> AppendNoteAsync(string path, string content, CancellationToken cancellationToken = default)
        => EnqueueAsync(new AppendNoteCommand(path, content), cancellationToken);

    public Task<VaultCommandResult> CopyNoteAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => EnqueueAsync(new CopyNoteCommand(sourcePath, destinationPath), cancellationToken);

    public Task<VaultCommandResult> SetPropertiesAsync(string path, Dictionary<string, object> properties, CancellationToken cancellationToken = default)
        => EnqueueAsync(new SetPropertiesCommand(path, properties), cancellationToken);

    public Task<VaultCommandResult> AddTagAsync(string path, string tag, CancellationToken cancellationToken = default)
        => EnqueueAsync(new AddTagCommand(path, tag), cancellationToken);

    public Task<VaultCommandResult> RemoveTagAsync(string path, string tag, CancellationToken cancellationToken = default)
        => EnqueueAsync(new RemoveTagCommand(path, tag), cancellationToken);

    public Task<VaultCommandResult> TransitionNoteAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => EnqueueAsync(new TransitionNoteCommand(sourcePath, destinationPath), cancellationToken);

    public Task<VaultCommandResult> DeleteNoteAsync(string path, CancellationToken cancellationToken = default)
        => EnqueueAsync(new DeleteNoteCommand(path), cancellationToken);

    public Task<VaultCommandResult> TransitionStatusAsync(string path, string newStatus, CancellationToken cancellationToken = default)
        => EnqueueAsync(new TransitionStatusCommand(path, newStatus), cancellationToken);

    private Task<VaultCommandResult> EnqueueAsync(VaultCommand command, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<VaultCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite(new VaultCommandEnvelope(command, tcs));
        return tcs.Task.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var commitTask = ProcessFossilCommits(stoppingToken);
        try
        {
            await foreach (var envelope in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var result = ExecuteCommand(envelope.Command);
                    envelope.Completion.SetResult(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Vault command failed: {Command}", envelope.Command.GetType().Name);
                    envelope.Completion.SetResult(new VaultCommandResult
                    {
                        Success = false,
                        Error = $"{ex.GetType().Name}: {ex.Message}"
                    });
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _commitSignals.Writer.TryComplete();
            try { await commitTask; } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private VaultCommandResult ExecuteCommand(VaultCommand command)
    {
        EnsureVaultReady();

        var result = command switch
        {
            CreateNoteCommand c => CreateNote(c),
            UpdateNoteCommand u => UpdateNote(u),
            AppendNoteCommand a => AppendNote(a),
            CopyNoteCommand c => CopyNote(c),
            SetPropertiesCommand s => SetProperties(s),
            AddTagCommand a => AddTag(a),
            RemoveTagCommand r => RemoveTag(r),
            TransitionNoteCommand t => TransitionNote(t),
            DeleteNoteCommand d => DeleteNote(d),
            TransitionStatusCommand s => TransitionStatus(s),
            _ => new VaultCommandResult { Success = false, Error = "Unknown command" }
        };

        return result;
    }

    private VaultCommandResult CreateNote(CreateNoteCommand command)
    {
        var fullPath = ValidateAndResolvePath(command.Path, mustExist: false);
        if (File.Exists(fullPath))
            return Fail($"Note already exists: {command.Path}");

        ValidateContent(command.Content);

        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null)
            Directory.CreateDirectory(directory);

        AtomicWrite(fullPath, command.Content);
        var relativePath = Path.GetRelativePath(_vaultPath, fullPath).Replace('\\', '/');
        _index.UpsertFile(fullPath, "create");
        Emit(new NoteCreatedEvent(relativePath));

        return Ok($"Created {relativePath}");
    }

    private VaultCommandResult UpdateNote(UpdateNoteCommand command)
    {
        var fullPath = ValidateAndResolvePath(command.Path, mustExist: false);
        var existed = File.Exists(fullPath);
        ValidateContent(command.Content);

        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null)
            Directory.CreateDirectory(directory);

        AtomicWrite(fullPath, command.Content);
        var relativePath = Path.GetRelativePath(_vaultPath, fullPath).Replace('\\', '/');
        _index.UpsertFile(fullPath, existed ? "update" : "create");
        Emit(existed ? new NoteUpdatedEvent(relativePath) : new NoteCreatedEvent(relativePath));

        return Ok($"{(existed ? "Updated" : "Created")} {relativePath}");
    }

    private VaultCommandResult AppendNote(AppendNoteCommand command)
    {
        var fullPath = ValidateAndResolvePath(command.Path, mustExist: false);
        ValidateContent(command.Content);

        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null)
            Directory.CreateDirectory(directory);

        string content;
        if (File.Exists(fullPath))
        {
            var existing = File.ReadAllText(fullPath);
            content = existing + "\n\n" + command.Content;
        }
        else
        {
            content = command.Content;
        }

        AtomicWrite(fullPath, content);
        var relativePath = Path.GetRelativePath(_vaultPath, fullPath).Replace('\\', '/');
        _index.UpsertFile(fullPath, "append");
        Emit(new NoteUpdatedEvent(relativePath));

        return Ok($"Appended to {relativePath}");
    }

    private VaultCommandResult CopyNote(CopyNoteCommand command)
    {
        var sourceFullPath = ValidateAndResolvePath(command.SourcePath, mustExist: true);
        var destinationFullPath = ValidateAndResolvePath(command.DestinationPath, mustExist: false);
        if (File.Exists(destinationFullPath))
            return Fail($"Destination note already exists: {command.DestinationPath}");

        var content = File.ReadAllText(sourceFullPath);
        ValidateContent(content);

        var destinationDirectory = Path.GetDirectoryName(destinationFullPath);
        if (destinationDirectory != null)
            Directory.CreateDirectory(destinationDirectory);

        AtomicWrite(destinationFullPath, content);
        var sourceRelative = Path.GetRelativePath(_vaultPath, sourceFullPath).Replace('\\', '/');
        var destinationRelative = Path.GetRelativePath(_vaultPath, destinationFullPath).Replace('\\', '/');
        _index.UpsertFile(destinationFullPath, "copy");
        Emit(new NoteCreatedEvent(destinationRelative));

        return Ok($"Copied {sourceRelative} to {destinationRelative}");
    }

    private VaultCommandResult SetProperties(SetPropertiesCommand command)
    {
        var fullPath = ValidateAndResolvePath(command.Path, mustExist: false);
        var existed = File.Exists(fullPath);
        var content = existed ? File.ReadAllText(fullPath) : string.Empty;
        var newContent = FrontmatterParser.Update(content, command.Properties);
        return WriteTransformedNote(fullPath, newContent, existed ? "set-properties" : "create", $"Updated properties on {command.Path}", existed);
    }

    private VaultCommandResult AddTag(AddTagCommand command)
    {
        var fullPath = ValidateAndResolvePath(command.Path, mustExist: true);
        var content = File.ReadAllText(fullPath);
        var props = FrontmatterParser.Parse(content) ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var tags = GetTags(props);
        if (!tags.Contains(command.Tag, StringComparer.OrdinalIgnoreCase))
            tags.Add(command.Tag);
        props["tags"] = tags;
        var newContent = FrontmatterParser.Update(content, props);
        return WriteTransformedNote(fullPath, newContent, "add-tag", $"Added tag '{command.Tag}' to {command.Path}");
    }

    private VaultCommandResult RemoveTag(RemoveTagCommand command)
    {
        var fullPath = ValidateAndResolvePath(command.Path, mustExist: true);
        var content = File.ReadAllText(fullPath);
        var props = FrontmatterParser.Parse(content);
        if (props == null || !props.ContainsKey("tags"))
            return Ok($"Tag '{command.Tag}' not found on {command.Path}");

        var tags = GetTags(props);
        var removed = tags.RemoveAll(tag => tag.Equals(command.Tag, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return Ok($"Tag '{command.Tag}' not found on {command.Path}");

        if (tags.Count == 0)
            props.Remove("tags");
        else
            props["tags"] = tags;
        var newContent = FrontmatterParser.Update(content, props);
        return WriteTransformedNote(fullPath, newContent, "remove-tag", $"Removed tag '{command.Tag}' from {command.Path}");
    }

    private VaultCommandResult WriteTransformedNote(string fullPath, string content, string operation, string message, bool existed = true)
    {
        ValidateContent(content);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null)
            Directory.CreateDirectory(directory);
        AtomicWrite(fullPath, content);
        var relativePath = Path.GetRelativePath(_vaultPath, fullPath).Replace('\\', '/');
        _index.UpsertFile(fullPath, operation);
        Emit(existed ? new NoteUpdatedEvent(relativePath) : new NoteCreatedEvent(relativePath));
        return Ok(message);
    }

    private static List<string> GetTags(Dictionary<string, object> props)
    {
        if (!props.TryGetValue("tags", out var value) || value == null)
            return new List<string>();
        if (value is string tag)
            return new List<string> { tag };
        if (value is IList list)
            return list.Cast<object>().Select(item => item?.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToList();
        return new List<string>();
    }

    private VaultCommandResult TransitionNote(TransitionNoteCommand command)
    {
        var sourceFullPath = ValidateAndResolvePath(command.SourcePath, mustExist: true);
        var destinationFullPath = ValidateAndResolvePath(command.DestinationPath, mustExist: false);

        if (Directory.Exists(destinationFullPath) || !Path.HasExtension(command.DestinationPath))
            destinationFullPath = Path.Combine(destinationFullPath, Path.GetFileName(sourceFullPath));

        if (File.Exists(destinationFullPath))
            return Fail($"Destination note already exists: {command.DestinationPath}");

        var destinationDirectory = Path.GetDirectoryName(destinationFullPath);
        if (destinationDirectory != null)
            Directory.CreateDirectory(destinationDirectory);

        File.Move(sourceFullPath, destinationFullPath);
        _index.RenameFile(sourceFullPath, destinationFullPath, "transition");

        var sourceRelative = Path.GetRelativePath(_vaultPath, sourceFullPath).Replace('\\', '/');
        var destinationRelative = Path.GetRelativePath(_vaultPath, destinationFullPath).Replace('\\', '/');
        Emit(new NoteTransitionedEvent(sourceRelative, destinationRelative));

        return Ok($"Moved {sourceRelative} to {destinationRelative}");
    }

    private VaultCommandResult TransitionStatus(TransitionStatusCommand command)
    {
        var fullPath = ValidateAndResolvePath(command.Path, mustExist: true);
        var content = File.ReadAllText(fullPath);
        var props = FrontmatterParser.Parse(content) ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        ValidateStatusTransition(command.Path, command.NewStatus, props);
        props["status"] = command.NewStatus;

        var newContent = FrontmatterParser.Update(content, props);
        ValidateContent(newContent);
        AtomicWrite(fullPath, newContent);

        var relativePath = Path.GetRelativePath(_vaultPath, fullPath).Replace('\\', '/');
        _index.UpsertFile(fullPath, "status-transition");
        Emit(new NoteUpdatedEvent(relativePath));

        return Ok($"Transitioned {relativePath} to status '{command.NewStatus}'");
    }

    private VaultCommandResult DeleteNote(DeleteNoteCommand command)
    {
        var fullPath = ValidateAndResolvePath(command.Path, mustExist: true);
        var relativePath = Path.GetRelativePath(_vaultPath, fullPath).Replace('\\', '/');

        if (relativePath.Equals(".trash", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith(".trash/", StringComparison.OrdinalIgnoreCase))
            return Fail("Cannot delete a note that is already in .trash");

        var trashPath = Path.Combine(_vaultPath, ".trash", relativePath);
        if (File.Exists(trashPath))
        {
            var directory = Path.GetDirectoryName(trashPath) ?? Path.Combine(_vaultPath, ".trash");
            var fileName = Path.GetFileNameWithoutExtension(trashPath);
            var extension = Path.GetExtension(trashPath);
            trashPath = Path.Combine(directory, fileName + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + extension);
        }

        var trashDirectory = Path.GetDirectoryName(trashPath);
        if (trashDirectory != null)
            Directory.CreateDirectory(trashDirectory);

        File.Move(fullPath, trashPath);
        _index.RenameFile(fullPath, trashPath, "delete");

        var trashRelative = Path.GetRelativePath(_vaultPath, trashPath).Replace('\\', '/');
        Emit(new NoteTransitionedEvent(relativePath, trashRelative));

        return Ok($"Moved {relativePath} to {trashRelative}");
    }

    private void EnsureVaultReady()
    {
        if (string.IsNullOrWhiteSpace(_vaultPath))
            throw new InvalidOperationException("Vault path not configured. Set OBSIDIAN_VAULT_PATH or VaultPath in appsettings.json.");
        if (!Directory.Exists(_vaultPath))
            throw new DirectoryNotFoundException("Obsidian vault not found at: " + _vaultPath);
    }

    private string ValidateAndResolvePath(string relativePath, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Path cannot be empty", nameof(relativePath));

        var fullPath = Path.GetFullPath(Path.Combine(_vaultPath, relativePath));
        var vaultRoot = Path.GetFullPath(_vaultPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var vaultFull = Path.GetFullPath(_vaultPath);

        if (!fullPath.Equals(vaultFull, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(vaultRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path outside vault");

        if (!Path.GetExtension(fullPath).Equals(".md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path must be a Markdown file (.md)");

        var normalizedPath = Path.GetRelativePath(_vaultPath, fullPath).Replace('\\', '/');
        if (normalizedPath.StartsWith(".trash/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Direct writes to .trash are not allowed");

        if (mustExist && !File.Exists(fullPath))
            throw new FileNotFoundException("Note not found", relativePath);

        return fullPath;
    }

    private static void ValidateContent(string content)
    {
        if (content == null)
            throw new ArgumentNullException(nameof(content), "Note content cannot be null");

        var props = FrontmatterParser.Parse(content);
        if (props != null)
        {
            var typeObj = props.TryGetValue("type", out var t) ? t?.ToString() : null;
            var errors = VaultSchema.ValidateFrontmatter(typeObj, props);
            if (errors.Length > 0)
                throw new InvalidOperationException("Schema validation failed: " + string.Join("; ", errors));
        }
    }

    private static void ValidateStatusTransition(string notePath, string newStatus, Dictionary<string, object> props)
    {
        var typeObj = props.TryGetValue("type", out var t) ? t?.ToString() : null;
        var currentStatus = props.TryGetValue("status", out var s) ? s?.ToString() : null;
        if (!VaultSchema.IsValidNoteType(typeObj))
            return;

        if (!VaultSchema.IsValidStatusTransition(typeObj!, currentStatus, newStatus))
            throw new InvalidOperationException(
                $"Invalid status transition for '{typeObj}' from '{currentStatus}' to '{newStatus}'. Allowed flow: {string.Join(" -> ", VaultSchema.StatusFlows[typeObj!])}");
    }

    private static void AtomicWrite(string fullPath, string content)
    {
        var directory = Path.GetDirectoryName(fullPath)!;
        var tempPath = Path.Combine(directory, $".tmp-{Guid.NewGuid()}.md");
        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private void Emit(VaultEvent @event)
    {
        if (!string.IsNullOrWhiteSpace(_fossilRepo))
        {
            lock (_batchLock)
            {
                _pendingOperationCount++;
                _pendingPaths.Add(@event.NotePath);
                if (@event is NoteTransitionedEvent transitioned)
                    _pendingPaths.Add(transitioned.SourcePath);
            }
            _commitSignals.Writer.TryWrite(true);
        }

        try
        {
            VaultChanged?.Invoke(@event);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault event handler failed");
        }
    }

    private async Task ProcessFossilCommits(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_fossilRepo))
            return;

        await foreach (var signal in _commitSignals.Reader.ReadAllAsync(stoppingToken))
        {
            _ = signal;
            await Task.Delay(_fossilBatchDelay, stoppingToken);
            while (_commitSignals.Reader.TryRead(out _)) { }
            await TryFossilCommitAsync(stoppingToken);
        }
    }

    private async Task TryFossilCommitAsync(CancellationToken stoppingToken)
    {
        int operationCount;
        string[] paths;
        lock (_batchLock)
        {
            operationCount = _pendingOperationCount;
            paths = _pendingPaths.ToArray();
        }
        if (operationCount == 0)
            return;

        try
        {
            var existingPaths = paths
                .Where(path => File.Exists(Path.Combine(_vaultPath, path)))
                .Select(path => "./" + path.Replace('\\', '/'))
                .ToArray();
            if (existingPaths.Length > 0)
            {
                var addArguments = new List<string> { "add", "--dotfiles", "--force" };
                addArguments.AddRange(existingPaths);
                var addResult = await RunFossilAsync(addArguments, stoppingToken);
                if (addResult.ExitCode != 0)
                {
                    _logger.LogWarning("Fossil add exited with {ExitCode}: {Error}", addResult.ExitCode, addResult.Detail);
                    return;
                }
            }

            var commitArguments = new List<string> { "commit" };
            if (File.Exists(_fossilRepo) || _fossilRepo!.EndsWith(".fossil", StringComparison.OrdinalIgnoreCase))
            {
                commitArguments.Add("-R");
                commitArguments.Add(_fossilRepo);
            }
            commitArguments.Add("--no-warnings");
            commitArguments.Add("--if-changes");
            commitArguments.Add("-m");
            commitArguments.Add($"aimcp batch ({operationCount} operations)");

            var commitResult = await RunFossilAsync(commitArguments, stoppingToken);
            if (commitResult.ExitCode != 0)
            {
                _logger.LogWarning("Fossil commit exited with {ExitCode}: {Error}", commitResult.ExitCode, commitResult.Detail);
                return;
            }

            _logger.LogInformation("Fossil commit succeeded for {OperationCount} operations", operationCount);
            lock (_batchLock)
            {
                _pendingOperationCount = Math.Max(0, _pendingOperationCount - operationCount);
                foreach (var path in paths)
                    _pendingPaths.Remove(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fossil commit failed");
        }
    }

    private async Task<FossilResult> RunFossilAsync(IEnumerable<string> arguments, CancellationToken stoppingToken)
    {
        var psi = new ProcessStartInfo("fossil")
        {
            WorkingDirectory = _vaultPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start fossil process");
        var outputTask = process.StandardOutput.ReadToEndAsync(stoppingToken);
        var errorTask = process.StandardError.ReadToEndAsync(stoppingToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new FossilResult(-1, "Fossil command timed out after 30 seconds");
        }

        var output = await outputTask;
        var error = await errorTask;
        return new FossilResult(process.ExitCode, (string.IsNullOrWhiteSpace(error) ? output : error).Trim());
    }

    private static VaultCommandResult Ok(string message) => new() { Success = true, Message = message };
    private static VaultCommandResult Fail(string error) => new() { Success = false, Error = error };

    private sealed record VaultCommandEnvelope(VaultCommand Command, TaskCompletionSource<VaultCommandResult> Completion);
    private sealed record FossilResult(int ExitCode, string Detail);
}
