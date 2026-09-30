[McpServerToolType]
public class ObsidianTools
{
    private readonly VaultIndex _index;
    private readonly VaultKernel _kernel;

    public ObsidianTools(IConfiguration config, VaultIndex index, VaultKernel kernel)
    {
        // Prefer env var for VPS/Docker. Do NOT throw if vault is missing here —
        // throwing in the ctor can break DI / tool resolution for some clients.
        // Fail later with a clear message when a tool is actually called.
        _index = index;
        _kernel = kernel;
    }

    private static Exception ToolError(string operation, string context, Exception ex)
    {
        return new InvalidOperationException(
            $"{operation} failed ({context}). {ex.GetType().Name}: {ex.Message}", ex);
    }

    [McpServerTool, Description("List Markdown notes in the Obsidian vault. Use folder='' for root.")]
    public Task<List<string>> ListNotes(string folder = "")
    {
        try
        {
            var notes = _index.ListNotes(folder);
            return Task.FromResult(notes);
        }
        catch (Exception ex)
        {
            throw ToolError("List notes", $"folder='{folder}'", ex);
        }
    }

    [McpServerTool, Description("Read the full content of a note. Path is relative to vault root (e.g. 'Daily/2025-05-12.md')")]
    public async Task<string> ReadNote([Description("Relative path to the note inside the vault")] string notePath)
    {
        try
        {
            var fullPath = _index.GetFullPath(notePath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Note not found", notePath);
            return await File.ReadAllTextAsync(fullPath);
        }
        catch (Exception ex)
        {
            throw ToolError("Read note", $"path='{notePath}'", ex);
        }
    }

    [McpServerTool, Description("Full-text search across all notes using the FTS5 index.")]
    public Task<List<SearchResult>> SearchNotes([Description("The text to search for")] string query, int maxResults = 10)
    {
        try
        {
            var results = _index.SearchNotes(query, maxResults);
            return Task.FromResult(results);
        }
        catch (Exception ex)
        {
            throw ToolError("Search notes", $"query='{query}', maxResults={maxResults}", ex);
        }
    }

    [McpServerTool, Description("Append text to an existing note (or create it if it doesn't exist)")]
    public async Task<string> AppendToNote([Description("Relative path to the note")] string notePath, [Description("Text to append")] string content)
    {
        try
        {
            var result = await _kernel.AppendNoteAsync(notePath, content);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "Append failed");
            return result.Message;
        }
        catch (Exception ex)
        {
            throw ToolError("Append to note", $"path='{notePath}'", ex);
        }
    }

    [McpServerTool, Description("Create or overwrite a note with the given content")]
    public async Task<string> WriteNote([Description("Relative path to the note")] string notePath, [Description("Full content of the note")] string content)
    {
        try
        {
            var result = await _kernel.UpdateNoteAsync(notePath, content);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "Write failed");
            return result.Message;
        }
        catch (Exception ex)
        {
            throw ToolError("Write note", $"path='{notePath}'", ex);
        }
    }

    [McpServerTool, Description("Copy a note to another path in the vault. Fails if the destination already exists.")]
    public async Task<string> CopyNote([Description("Relative path of the note to copy")] string sourcePath, [Description("Relative destination path for the copy")] string destinationPath)
    {
        try
        {
            var result = await _kernel.CopyNoteAsync(sourcePath, destinationPath);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "Copy failed");
            return result.Message;
        }
        catch (Exception ex)
        {
            throw ToolError("Copy note", $"source='{sourcePath}', destination='{destinationPath}'", ex);
        }
    }

    [McpServerTool, Description("Move or rename a note within the vault. A directory or extensionless destination is treated as a folder. Fails if the destination note already exists.")]
    public async Task<string> MoveNote([Description("Relative path of the note to move")] string sourcePath, [Description("Relative destination note path or folder")] string destinationPath)
    {
        try
        {
            var result = await _kernel.TransitionNoteAsync(sourcePath, destinationPath);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "Move failed");
            return result.Message;
        }
        catch (Exception ex)
        {
            throw ToolError("Move note", $"source='{sourcePath}', destination='{destinationPath}'", ex);
        }
    }

    [McpServerTool, Description("Soft-delete a note by moving it into the vault's top-level .trash folder.")]
    public async Task<string> DeleteNote([Description("Relative path of the note to move to .trash")] string notePath)
    {
        try
        {
            var result = await _kernel.DeleteNoteAsync(notePath);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "Delete failed");
            return result.Message;
        }
        catch (Exception ex)
        {
            throw ToolError("Delete note", $"path='{notePath}'", ex);
        }
    }

    [McpServerTool, Description("Get YAML frontmatter properties from a note. Returns null if no frontmatter exists.")]
    public async Task<Dictionary<string, object>?> GetNoteProperties([Description("Relative path to the note")] string notePath)
    {
        try
        {
            var fullPath = _index.GetFullPath(notePath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Note not found", notePath);

            var content = await File.ReadAllTextAsync(fullPath);
            return FrontmatterParser.Parse(content);
        }
        catch (Exception ex)
        {
            throw ToolError("Get note properties", $"path='{notePath}'", ex);
        }
    }

    [McpServerTool, Description("Get tags from a note's YAML frontmatter. Returns empty list if no tags or no frontmatter.")]
    public async Task<List<string>> GetNoteTags([Description("Relative path to the note")] string notePath)
    {
        try
        {
            var tags = _index.GetNoteTags(notePath);
            return await Task.FromResult(tags);
        }
        catch (Exception ex)
        {
            throw ToolError("Get note tags", $"path='{notePath}'", ex);
        }
    }

    [McpServerTool, Description("List all unique tags across all notes in the vault.")]
    public Task<List<string>> ListAllTags()
    {
        try
        {
            var tags = _index.ListAllTags();
            return Task.FromResult(tags);
        }
        catch (Exception ex)
        {
            throw ToolError("List all tags", "", ex);
        }
    }

    [McpServerTool, Description("Search for notes that contain a specific tag in their YAML frontmatter.")]
    public Task<List<string>> SearchByTag([Description("Tag to search for")] string tag, int maxResults = 50)
    {
        try
        {
            var results = _index.SearchByTag(tag, maxResults);
            return Task.FromResult(results);
        }
        catch (Exception ex)
        {
            throw ToolError("Search by tag", $"tag='{tag}', maxResults={maxResults}", ex);
        }
    }

    [McpServerTool, Description("Transition a note's status through its type's state machine.")]
    public async Task<string> TransitionNote([Description("Relative path to the note")] string notePath, [Description("New status value")] string newStatus)
    {
        try
        {
            var result = await _kernel.TransitionStatusAsync(notePath, newStatus);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "Transition failed");
            return result.Message;
        }
        catch (Exception ex)
        {
            throw ToolError("Transition note", $"path='{notePath}', newStatus='{newStatus}'", ex);
        }
    }

    [McpServerTool, Description("Query the MCP index history for a note.")]
    public Task<List<HistoryRecord>> QueryHistory([Description("Relative path to the note")] string notePath, int limit = 50)
    {
        try
        {
            var results = _index.QueryHistory(notePath, limit);
            return Task.FromResult(results);
        }
        catch (Exception ex)
        {
            throw ToolError("Query history", $"path='{notePath}', limit={limit}", ex);
        }
    }

    [McpServerTool, Description("List notes that link to the given note (backlinks).")]
    public Task<List<string>> LinksTo([Description("Relative path to the note")] string notePath)
    {
        try
        {
            var results = _index.GetLinksTo(notePath);
            return Task.FromResult(results);
        }
        catch (Exception ex)
        {
            throw ToolError("Links to", $"path='{notePath}'", ex);
        }
    }

    [McpServerTool, Description("List orphan notes with no incoming or outgoing links.")]
    public Task<List<OrphanResult>> Orphans()
    {
        try
        {
            var results = _index.GetOrphans();
            return Task.FromResult(results);
        }
        catch (Exception ex)
        {
            throw ToolError("Orphans", "", ex);
        }
    }

    [McpServerTool, Description("List notes that have not been modified in a given number of days.")]
    public Task<List<StalledResult>> Stalled([Description("Days without edits before a note is considered stalled")] int daysWithoutEdit = 30)
    {
        try
        {
            var results = _index.GetStalled(daysWithoutEdit);
            return Task.FromResult(results);
        }
        catch (Exception ex)
        {
            throw ToolError("Stalled", $"days={daysWithoutEdit}", ex);
        }
    }

    [McpServerTool, Description("Validate a note's frontmatter against the vault schema.")]
    public async Task<string> ValidateSchema([Description("Relative path to the note; empty checks all notes")] string notePath = "")
    {
        try
        {
            var paths = string.IsNullOrWhiteSpace(notePath)
                ? _index.ListNotes()
                : new List<string> { notePath };

            var errors = new List<string>();
            foreach (var path in paths)
            {
                var content = _index.ReadNoteContent(path);
                if (content == null) continue;
                var props = FrontmatterParser.Parse(content);
                if (props == null)
                {
                    errors.Add($"{path}: no frontmatter");
                    continue;
                }
                var type = props.TryGetValue("type", out var t) ? t?.ToString() : null;
                var validationErrors = VaultSchema.ValidateFrontmatter(type, props);
                if (validationErrors.Length > 0)
                    errors.Add($"{path}: {string.Join("; ", validationErrors)}");
            }

            if (errors.Count == 0)
                return "All notes pass schema validation.";

            return "Schema validation failures:\n" + string.Join("\n", errors);
        }
        catch (Exception ex)
        {
            throw ToolError("Validate schema", $"path='{notePath}'", ex);
        }
    }

    [McpServerTool, Description("Generate a digest of recent vault activity, orphans, stalled notes, and schema status.")]
    public async Task<string> Digest([Description("Days to look back for recent changes")] int days = 7)
    {
        try
        {
            var since = DateTime.UtcNow.AddDays(-days).Ticks;
            var recent = _index.QueryRecentHistory(since);
            var orphans = _index.GetOrphans();
            var stalled = _index.GetStalled(30);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# Vault Digest (last {days} days)");
            sb.AppendLine($"Recent operations: {recent.Count}");
            foreach (var r in recent.Take(20))
                sb.AppendLine($"- {r.ChangedAt:u} {r.Operation} {r.NotePath}");

            sb.AppendLine($"Orphans: {orphans.Count}");
            foreach (var o in orphans.Take(20))
                sb.AppendLine($"- {o.NotePath}");

            sb.AppendLine($"Stalled (>30d): {stalled.Count}");
            foreach (var s in stalled.Take(20))
                sb.AppendLine($"- {s.NotePath} (last {s.LastModified:u})");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            throw ToolError("Digest", $"days={days}", ex);
        }
    }

    [McpServerTool, Description("Set or update YAML frontmatter properties on a note. Creates frontmatter if none exists.")]
    public async Task<string> SetNoteProperties([Description("Relative path to the note")] string notePath, [Description("Dictionary of property names to values")] Dictionary<string, object> properties)
    {
        try
        {
            var result = await _kernel.SetPropertiesAsync(notePath, properties);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "Set properties failed");
            return result.Message;
        }
        catch (Exception ex)
        {
            throw ToolError("Set note properties", $"path='{notePath}'", ex);
        }
    }

    [McpServerTool, Description("Add a tag to a note's YAML frontmatter. Creates frontmatter and tags array if needed.")]
    public async Task<string> AddTag([Description("Relative path to the note")] string notePath, [Description("Tag to add")] string tag)
    {
        try
        {
            var result = await _kernel.AddTagAsync(notePath, tag);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "Add tag failed");
            return result.Message;
        }
        catch (Exception ex)
        {
            throw ToolError("Add tag", $"path='{notePath}', tag='{tag}'", ex);
        }
    }

    [McpServerTool, Description("Remove a tag from a note's YAML frontmatter.")]
    public async Task<string> RemoveTag([Description("Relative path to the note")] string notePath, [Description("Tag to remove")] string tag)
    {
        try
        {
            var result = await _kernel.RemoveTagAsync(notePath, tag);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "Remove tag failed");
            return result.Message;
        }
        catch (Exception ex)
        {
            throw ToolError("Remove tag", $"path='{notePath}', tag='{tag}'", ex);
        }
    }
}

public class SearchResult
{
    public string NotePath { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
}
