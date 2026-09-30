public abstract record VaultEvent(string NotePath);

public sealed record NoteCreatedEvent(string NotePath) : VaultEvent(NotePath);
public sealed record NoteUpdatedEvent(string NotePath) : VaultEvent(NotePath);
public sealed record NoteTransitionedEvent(string SourcePath, string DestinationPath) : VaultEvent(DestinationPath);
public sealed record NoteDeletedEvent(string NotePath) : VaultEvent(NotePath);
