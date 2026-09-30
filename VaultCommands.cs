public abstract record VaultCommand;

public sealed record CreateNoteCommand(string Path, string Content) : VaultCommand;
public sealed record UpdateNoteCommand(string Path, string Content) : VaultCommand;
public sealed record AppendNoteCommand(string Path, string Content) : VaultCommand;
public sealed record CopyNoteCommand(string SourcePath, string DestinationPath) : VaultCommand;
public sealed record SetPropertiesCommand(string Path, Dictionary<string, object> Properties) : VaultCommand;
public sealed record AddTagCommand(string Path, string Tag) : VaultCommand;
public sealed record RemoveTagCommand(string Path, string Tag) : VaultCommand;
public sealed record TransitionNoteCommand(string SourcePath, string DestinationPath) : VaultCommand;
public sealed record DeleteNoteCommand(string Path) : VaultCommand;
public sealed record TransitionStatusCommand(string Path, string NewStatus) : VaultCommand;

public sealed class VaultCommandResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Error { get; init; }
}
