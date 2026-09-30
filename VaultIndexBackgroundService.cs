using System.Collections.Concurrent;

public sealed class VaultIndexBackgroundService : BackgroundService
{
    private readonly VaultIndex _index;
    private readonly VaultKernel _kernel;
    private readonly ILogger<VaultIndexBackgroundService> _logger;
    private FileSystemWatcher? _watcher;
    private readonly Channel<FileChange> _pendingChanges = Channel.CreateUnbounded<FileChange>();
    private readonly ConcurrentDictionary<string, DateTime> _kernelChanges = new(StringComparer.OrdinalIgnoreCase);

    public VaultIndexBackgroundService(VaultIndex index, VaultKernel kernel, ILogger<VaultIndexBackgroundService> logger)
    {
        _index = index;
        _kernel = kernel;
        _logger = logger;
        _kernel.VaultChanged += OnVaultChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _index.FullRebuild();
            _logger.LogInformation("Vault index rebuilt for {VaultPath}", _index.VaultPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build initial vault index");
        }

        try
        {
            if (Directory.Exists(_index.VaultPath))
            {
                _watcher = new FileSystemWatcher(_index.VaultPath, "*.md")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
                };
                _watcher.Created += OnFileChanged;
                _watcher.Changed += OnFileChanged;
                _watcher.Deleted += OnFileDeleted;
                _watcher.Renamed += OnFileRenamed;
                _watcher.EnableRaisingEvents = true;
                _logger.LogInformation("FileSystemWatcher started for {VaultPath}", _index.VaultPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start FileSystemWatcher");
        }

        await ProcessChanges(stoppingToken);
    }

    public override void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _kernel.VaultChanged -= OnVaultChanged;
        _pendingChanges.Writer.TryComplete();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnVaultChanged(VaultEvent @event)
    {
        var expiresAt = DateTime.UtcNow.AddSeconds(5);
        MarkKernelPath(@event.NotePath, expiresAt);
        if (@event is NoteTransitionedEvent transitioned)
            MarkKernelPath(transitioned.SourcePath, expiresAt);
    }

    private void MarkKernelPath(string relativePath, DateTime expiresAt)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_index.VaultPath, relativePath));
        _kernelChanges[fullPath] = expiresAt;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
        => _pendingChanges.Writer.TryWrite(new FileChange(FileChangeKind.Upsert, e.FullPath));

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
        => _pendingChanges.Writer.TryWrite(new FileChange(FileChangeKind.Delete, e.FullPath));

    private void OnFileRenamed(object sender, RenamedEventArgs e)
        => _pendingChanges.Writer.TryWrite(new FileChange(FileChangeKind.Rename, e.FullPath, e.OldFullPath));

    private async Task ProcessChanges(CancellationToken stoppingToken)
    {
        try
        {
            while (await _pendingChanges.Reader.WaitToReadAsync(stoppingToken))
            {
                var changes = new List<FileChange>();
                while (_pendingChanges.Reader.TryRead(out var change))
                    changes.Add(change);

                await Task.Delay(500, stoppingToken);
                while (_pendingChanges.Reader.TryRead(out var change))
                    changes.Add(change);

                foreach (var item in changes)
                    ApplyChange(item);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void ApplyChange(FileChange change)
    {
        try
        {
            if (IsKernelChangeAlreadyIndexed(change))
            {
                _logger.LogDebug("Suppressed kernel-originated {Kind} event for {Path}", change.Kind, change.Path);
                return;
            }

            switch (change.Kind)
            {
                case FileChangeKind.Upsert:
                    _index.UpsertFile(change.Path, "external-update");
                    break;
                case FileChangeKind.Delete:
                    _index.RemoveFile(change.Path, "external-delete");
                    break;
                case FileChangeKind.Rename:
                    _index.RenameFile(change.OldPath!, change.Path, "external-rename");
                    break;
            }
            _logger.LogDebug("Applied {Kind} index change for {Path}", change.Kind, change.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply {Kind} index change for {Path}", change.Kind, change.Path);
        }
    }

    private bool IsKernelChangeAlreadyIndexed(FileChange change)
    {
        var now = DateTime.UtcNow;
        foreach (var item in _kernelChanges)
        {
            if (item.Value <= now)
                _kernelChanges.TryRemove(item.Key, out _);
        }

        var pathMarked = _kernelChanges.TryGetValue(change.Path, out var expiresAt) && expiresAt > now;
        var oldPathMarked = change.OldPath != null
                            && _kernelChanges.TryGetValue(change.OldPath, out var oldExpiresAt)
                            && oldExpiresAt > now;
        if (!pathMarked && !oldPathMarked)
            return false;

        return change.Kind switch
        {
            FileChangeKind.Rename => _index.IsFileCurrent(change.OldPath!) && _index.IsFileCurrent(change.Path),
            _ => _index.IsFileCurrent(change.Path)
        };
    }

    private enum FileChangeKind
    {
        Upsert,
        Delete,
        Rename
    }

    private sealed record FileChange(FileChangeKind Kind, string Path, string? OldPath = null);
}
