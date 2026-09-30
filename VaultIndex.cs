using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed partial class VaultIndex : IDisposable
{
    private readonly string _vaultPath;
    private readonly string _indexPath;
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _lock = new();
    private bool _initialized;

    public VaultIndex(IConfiguration config)
    {
        _vaultPath = Environment.GetEnvironmentVariable("OBSIDIAN_VAULT_PATH")
                     ?? config["VaultPath"]
                     ?? string.Empty;
        _indexPath = Environment.GetEnvironmentVariable("AIMCP_INDEX_PATH")
                     ?? config["IndexPath"]
                     ?? Path.Combine(AppContext.BaseDirectory, "aimcp-index.db");
        _connectionString = $"Data Source={_indexPath};Mode=ReadWriteCreate;Foreign Keys=False";
    }

    public string VaultPath => _vaultPath;

    public void Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
            using var connection = OpenConnection();
            CreateSchema(connection);
            _initialized = true;
        }
    }

    public void EnsureReady()
    {
        if (string.IsNullOrWhiteSpace(_vaultPath))
            throw new InvalidOperationException("Vault path not configured. Set OBSIDIAN_VAULT_PATH or VaultPath in appsettings.json.");
        if (!Directory.Exists(_vaultPath))
            throw new DirectoryNotFoundException("Obsidian vault not found at: " + _vaultPath);
        Initialize();
    }

    public string GetFullPath(string relativePath)
    {
        EnsureReady();
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Path cannot be empty", nameof(relativePath));

        var fullPath = Path.GetFullPath(Path.Combine(_vaultPath, relativePath));
        var vaultRoot = Path.GetFullPath(_vaultPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var vaultFull = Path.GetFullPath(_vaultPath);

        if (!fullPath.Equals(vaultFull, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(vaultRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path outside vault");

        return fullPath;
    }

    public List<string> GetNoteTags(string notePath)
    {
        EnsureReady();
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT tag FROM tags WHERE note = @note ORDER BY tag";
            command.Parameters.AddWithValue("@note", notePath.Replace('\\', '/'));
            using var reader = command.ExecuteReader();
            var tags = new List<string>();
            while (reader.Read()) tags.Add(reader.GetString(0));
            return tags;
        }
    }

    public bool IsFileCurrent(string fullPath)
    {
        EnsureReady();
        var relativePath = Path.GetRelativePath(_vaultPath, fullPath).Replace('\\', '/');
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT hash FROM notes WHERE path = @path";
            command.Parameters.AddWithValue("@path", relativePath);
            var indexedHash = command.ExecuteScalar() as string;
            if (!File.Exists(fullPath) || !IsActiveNote(fullPath))
                return indexedHash == null;
            return indexedHash == ComputeHash(File.ReadAllText(fullPath));
        }
    }

    public void FullRebuild()
    {
        EnsureReady();
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            ClearAll(connection);
            foreach (var file in Directory.GetFiles(_vaultPath, "*.md", SearchOption.AllDirectories))
            {
                if (!IsActiveNote(file)) continue;
                try
                {
                    IndexFileInternal(connection, file, transaction);
                }
                catch { /* skip unreadable files */ }
            }
            transaction.Commit();
        }
    }

    public void UpsertFile(string fullPath, string? operation = null)
    {
        EnsureReady();
        if (!File.Exists(fullPath)) return;
        if (!IsActiveNote(fullPath))
        {
            RemoveFile(fullPath);
            return;
        }
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var relativePath = IndexFileInternal(connection, fullPath, transaction);
            if (!string.IsNullOrWhiteSpace(operation) && !string.IsNullOrWhiteSpace(relativePath))
            {
                var content = File.ReadAllText(fullPath);
                InsertHistory(connection, transaction, relativePath, operation, GetFrontmatterJson(content), ComputeHash(content));
            }
            transaction.Commit();
        }
    }

    public void RemoveFile(string fullPath, string? operation = null)
    {
        EnsureReady();
        var relativePath = Path.GetRelativePath(_vaultPath, fullPath).Replace('\\', '/');
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            if (!string.IsNullOrWhiteSpace(operation))
            {
                string? frontmatterJson = null;
                string? hash = null;
                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT frontmatter_json, hash FROM notes WHERE path = @path";
                    select.Parameters.AddWithValue("@path", relativePath);
                    using var reader = select.ExecuteReader();
                    if (reader.Read())
                    {
                        frontmatterJson = reader.IsDBNull(0) ? null : reader.GetString(0);
                        hash = reader.GetString(1);
                    }
                }
                if (hash != null)
                    InsertHistory(connection, transaction, relativePath, operation, frontmatterJson, hash);
            }
            DeleteNote(connection, relativePath, transaction);
            transaction.Commit();
        }
    }

    public void RenameFile(string oldFullPath, string newFullPath, string? operation = null)
    {
        EnsureReady();
        var oldRelativePath = Path.GetRelativePath(_vaultPath, oldFullPath);
        var newRelativePath = Path.GetRelativePath(_vaultPath, newFullPath);
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            DeleteNote(connection, oldRelativePath, transaction);
            if (File.Exists(newFullPath))
            {
                var content = File.ReadAllText(newFullPath);
                if (IsActiveNote(newFullPath))
                {
                    var indexedPath = IndexFileInternal(connection, newFullPath, transaction);
                    if (!string.IsNullOrWhiteSpace(operation) && !string.IsNullOrWhiteSpace(indexedPath))
                        InsertHistory(connection, transaction, indexedPath, operation, GetFrontmatterJson(content), ComputeHash(content));
                }
                else if (!string.IsNullOrWhiteSpace(operation))
                {
                    InsertHistory(connection, transaction, oldRelativePath.Replace('\\', '/'), operation, GetFrontmatterJson(content), ComputeHash(content));
                }
            }
            transaction.Commit();
        }
    }

    public List<SearchResult> SearchNotes(string query, int maxResults = 10)
    {
        EnsureReady();
        var results = new List<SearchResult>();
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT n.path, n.content
                FROM notes_fts fts
                JOIN notes n ON n.path = fts.path
                WHERE notes_fts MATCH @query
                ORDER BY rank
                LIMIT @limit";
            command.Parameters.AddWithValue("@query", query);
            command.Parameters.AddWithValue("@limit", maxResults);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var content = reader.GetString(1);
                var snippet = content.Length > 300 ? content[..300] + "..." : content;
                results.Add(new SearchResult { NotePath = path, Snippet = snippet });
            }
        }
        return results;
    }

    public List<string> ListAllTags()
    {
        EnsureReady();
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT DISTINCT tag FROM tags ORDER BY tag COLLATE NOCASE";
            using var reader = command.ExecuteReader();
            var tags = new List<string>();
            while (reader.Read()) tags.Add(reader.GetString(0));
            return tags;
        }
    }

    public List<string> SearchByTag(string tag, int maxResults = 50)
    {
        EnsureReady();
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT DISTINCT note FROM tags WHERE tag = @tag ORDER BY note LIMIT @limit";
            command.Parameters.AddWithValue("@tag", tag);
            command.Parameters.AddWithValue("@limit", maxResults);
            using var reader = command.ExecuteReader();
            var notes = new List<string>();
            while (reader.Read()) notes.Add(reader.GetString(0));
            return notes;
        }
    }

    public List<string> GetLinksTo(string notePath)
    {
        EnsureReady();
        var target = Path.GetFileNameWithoutExtension(notePath);
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT DISTINCT source FROM links
                WHERE target = @target
                   OR target = @path
                   OR target = @filename
                ORDER BY source";
            command.Parameters.AddWithValue("@target", target);
            command.Parameters.AddWithValue("@path", notePath.Replace('\\', '/'));
            command.Parameters.AddWithValue("@filename", Path.GetFileName(notePath));
            using var reader = command.ExecuteReader();
            var sources = new List<string>();
            while (reader.Read()) sources.Add(reader.GetString(0));
            return sources;
        }
    }

    public List<OrphanResult> GetOrphans()
    {
        EnsureReady();
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT n.path FROM notes n
                LEFT JOIN links l_in ON l_in.target = n.path
                LEFT JOIN links l_out ON l_out.source = n.path
                WHERE l_in.source IS NULL AND l_out.target IS NULL
                ORDER BY n.path";
            using var reader = command.ExecuteReader();
            var orphans = new List<OrphanResult>();
            while (reader.Read()) orphans.Add(new OrphanResult { NotePath = reader.GetString(0) });
            return orphans;
        }
    }

    public List<StalledResult> GetStalled(int daysWithoutEdit = 30)
    {
        EnsureReady();
        var cutoff = DateTime.UtcNow.AddDays(-daysWithoutEdit).Ticks;
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT path, mtime FROM notes
                WHERE mtime < @cutoff
                ORDER BY mtime";
            command.Parameters.AddWithValue("@cutoff", cutoff);
            using var reader = command.ExecuteReader();
            var stalled = new List<StalledResult>();
            while (reader.Read())
            {
                stalled.Add(new StalledResult
                {
                    NotePath = reader.GetString(0),
                    LastModified = new DateTime(reader.GetInt64(1), DateTimeKind.Utc)
                });
            }
            return stalled;
        }
    }

    public List<HistoryRecord> QueryRecentHistory(long sinceTicks)
    {
        EnsureReady();
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT note, changed_at, operation, frontmatter_json, content_hash
                FROM note_history
                WHERE changed_at >= @since
                ORDER BY changed_at DESC";
            command.Parameters.AddWithValue("@since", sinceTicks);
            using var reader = command.ExecuteReader();
            var records = new List<HistoryRecord>();
            while (reader.Read())
            {
                records.Add(new HistoryRecord
                {
                    NotePath = reader.GetString(0),
                    ChangedAt = new DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                    Operation = reader.GetString(2),
                    FrontmatterJson = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ContentHash = reader.GetString(4)
                });
            }
            return records;
        }
    }

    public List<HistoryRecord> QueryHistory(string notePath, int limit = 50)
    {
        EnsureReady();
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT changed_at, operation, frontmatter_json, content_hash
                FROM note_history
                WHERE note = @note
                ORDER BY changed_at DESC
                LIMIT @limit";
            command.Parameters.AddWithValue("@note", notePath.Replace('\\', '/'));
            command.Parameters.AddWithValue("@limit", limit);
            using var reader = command.ExecuteReader();
            var records = new List<HistoryRecord>();
            while (reader.Read())
            {
                records.Add(new HistoryRecord
                {
                    NotePath = notePath,
                    ChangedAt = new DateTime(reader.GetInt64(0), DateTimeKind.Utc),
                    Operation = reader.GetString(1),
                    FrontmatterJson = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ContentHash = reader.GetString(3)
                });
            }
            return records;
        }
    }

    public void RecordHistory(string notePath, string operation, string? frontmatterJson, string contentHash)
    {
        EnsureReady();
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            InsertHistory(connection, transaction, notePath, operation, frontmatterJson, contentHash);
            transaction.Commit();
        }
    }

    public string? ReadNoteContent(string notePath)
    {
        EnsureReady();
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT content FROM notes WHERE path = @path";
            command.Parameters.AddWithValue("@path", notePath.Replace('\\', '/'));
            return command.ExecuteScalar() as string;
        }
    }

    public List<string> ListNotes(string folder = "")
    {
        EnsureReady();
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            if (string.IsNullOrEmpty(folder))
            {
                command.CommandText = @"SELECT path FROM notes ORDER BY path";
            }
            else
            {
                command.CommandText = @"SELECT path FROM notes WHERE path LIKE @pattern ESCAPE '\' ORDER BY path";
                command.Parameters.AddWithValue("@pattern", EscapeLike(folder) + "/%");
            }
            using var reader = command.ExecuteReader();
            var notes = new List<string>();
            while (reader.Read()) notes.Add(reader.GetString(0));
            return notes;
        }
    }

    public void Dispose()
    {
        // Connections are opened/closed per operation; nothing to dispose at class level.
        GC.SuppressFinalize(this);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS notes (
                path TEXT PRIMARY KEY,
                frontmatter_json TEXT,
                mtime INTEGER NOT NULL,
                hash TEXT NOT NULL,
                content TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(
                path UNINDEXED,
                content
            );
            CREATE TABLE IF NOT EXISTS links (
                source TEXT NOT NULL,
                target TEXT NOT NULL,
                type TEXT NOT NULL,
                PRIMARY KEY (source, target, type)
            );
            CREATE TABLE IF NOT EXISTS tags (
                note TEXT NOT NULL,
                tag TEXT NOT NULL,
                PRIMARY KEY (note, tag)
            );
            CREATE TABLE IF NOT EXISTS note_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                note TEXT NOT NULL,
                changed_at INTEGER NOT NULL,
                operation TEXT NOT NULL,
                frontmatter_json TEXT,
                content_hash TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_history_note ON note_history(note, changed_at);";
        command.ExecuteNonQuery();
    }

    private static void ClearAll(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM notes;
            DELETE FROM links;
            DELETE FROM tags;
            DELETE FROM notes_fts;";
        command.ExecuteNonQuery();
    }

    private string? IndexFileInternal(SqliteConnection connection, string fullPath, SqliteTransaction transaction)
    {
        var relativePath = Path.GetRelativePath(_vaultPath, fullPath).Replace('\\', '/');
        var content = File.ReadAllText(fullPath);
        var mtime = new FileInfo(fullPath).LastWriteTimeUtc.Ticks;
        var hash = ComputeHash(content);

        // Skip if unchanged
        using var checkCommand = connection.CreateCommand();
        checkCommand.Transaction = transaction;
        checkCommand.CommandText = "SELECT hash FROM notes WHERE path = @path";
        checkCommand.Parameters.AddWithValue("@path", relativePath);
        var existing = checkCommand.ExecuteScalar();
        if (existing is string existingHash && existingHash == hash) return null;

        var props = FrontmatterParser.Parse(content);
        var frontmatterJson = props != null ? JsonSerializer.Serialize(props, _jsonOptions) : null;

        DeleteNote(connection, relativePath, transaction);

        using var insertNote = connection.CreateCommand();
        insertNote.Transaction = transaction;
        insertNote.CommandText = @"
            INSERT INTO notes (path, frontmatter_json, mtime, hash, content)
            VALUES (@path, @frontmatter_json, @mtime, @hash, @content)";
        insertNote.Parameters.AddWithValue("@path", relativePath);
        insertNote.Parameters.AddWithValue("@frontmatter_json", frontmatterJson ?? (object)DBNull.Value);
        insertNote.Parameters.AddWithValue("@mtime", mtime);
        insertNote.Parameters.AddWithValue("@hash", hash);
        insertNote.Parameters.AddWithValue("@content", content);
        insertNote.ExecuteNonQuery();

        using var insertFts = connection.CreateCommand();
        insertFts.Transaction = transaction;
        insertFts.CommandText = "INSERT INTO notes_fts (path, content) VALUES (@path, @content)";
        insertFts.Parameters.AddWithValue("@path", relativePath);
        insertFts.Parameters.AddWithValue("@content", content);
        insertFts.ExecuteNonQuery();

        ExtractLinks(relativePath, content);
        foreach (var link in _pendingLinks)
        {
            using var insertLink = connection.CreateCommand();
            insertLink.Transaction = transaction;
            insertLink.CommandText = "INSERT OR IGNORE INTO links (source, target, type) VALUES (@source, @target, @type)";
            insertLink.Parameters.AddWithValue("@source", link.Source);
            insertLink.Parameters.AddWithValue("@target", link.Target);
            insertLink.Parameters.AddWithValue("@type", link.Type);
            insertLink.ExecuteNonQuery();
        }
        _pendingLinks.Clear();

        if (props != null && props.TryGetValue("tags", out var tagsObj))
        {
            foreach (var tag in NormalizeTags(tagsObj))
            {
                using var insertTag = connection.CreateCommand();
                insertTag.Transaction = transaction;
                insertTag.CommandText = "INSERT OR IGNORE INTO tags (note, tag) VALUES (@note, @tag)";
                insertTag.Parameters.AddWithValue("@note", relativePath);
                insertTag.Parameters.AddWithValue("@tag", tag ?? (object)DBNull.Value);
                insertTag.ExecuteNonQuery();
            }
        }

        return relativePath;
    }

    private static void InsertHistory(SqliteConnection connection, SqliteTransaction transaction, string notePath, string operation, string? frontmatterJson, string contentHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO note_history (note, changed_at, operation, frontmatter_json, content_hash)
            VALUES (@note, @changed_at, @operation, @frontmatter_json, @content_hash)";
        command.Parameters.AddWithValue("@note", notePath.Replace('\\', '/'));
        command.Parameters.AddWithValue("@changed_at", DateTime.UtcNow.Ticks);
        command.Parameters.AddWithValue("@operation", operation);
        command.Parameters.AddWithValue("@frontmatter_json", frontmatterJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@content_hash", contentHash);
        command.ExecuteNonQuery();
    }

    private static void DeleteNote(SqliteConnection connection, string relativePath, SqliteTransaction transaction)
    {
        relativePath = relativePath.Replace('\\', '/');
        using var deleteNote = connection.CreateCommand();
        deleteNote.Transaction = transaction;
        deleteNote.CommandText = "DELETE FROM notes WHERE path = @path";
        deleteNote.Parameters.AddWithValue("@path", relativePath);
        deleteNote.ExecuteNonQuery();

        using var deleteFts = connection.CreateCommand();
        deleteFts.Transaction = transaction;
        deleteFts.CommandText = "DELETE FROM notes_fts WHERE path = @path";
        deleteFts.Parameters.AddWithValue("@path", relativePath);
        deleteFts.ExecuteNonQuery();

        using var deleteLinks = connection.CreateCommand();
        deleteLinks.Transaction = transaction;
        deleteLinks.CommandText = "DELETE FROM links WHERE source = @path";
        deleteLinks.Parameters.AddWithValue("@path", relativePath);
        deleteLinks.ExecuteNonQuery();

        using var deleteTags = connection.CreateCommand();
        deleteTags.Transaction = transaction;
        deleteTags.CommandText = "DELETE FROM tags WHERE note = @path";
        deleteTags.Parameters.AddWithValue("@path", relativePath);
        deleteTags.ExecuteNonQuery();
    }

    private readonly List<ObsidianLink> _pendingLinks = new();

    private void ExtractLinks(string sourcePath, string content)
    {
        var wikiMatches = WikiLinkRegex().Matches(content);
        foreach (Match match in wikiMatches)
        {
            var target = match.Groups[1].Value.Trim();
            _pendingLinks.Add(new ObsidianLink { Source = sourcePath, Target = target, Type = "wikilink" });
        }

        var mdMatches = MarkdownLinkRegex().Matches(content);
        foreach (Match match in mdMatches)
        {
            var target = match.Groups[2].Value.Trim();
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("#", StringComparison.Ordinal))
                continue;
            _pendingLinks.Add(new ObsidianLink { Source = sourcePath, Target = target, Type = "markdown" });
        }
    }

    private static string? GetFrontmatterJson(string content)
    {
        var props = FrontmatterParser.Parse(content);
        return props != null ? JsonSerializer.Serialize(props, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) : null;
    }

    private static IEnumerable<string> NormalizeTags(object tagsObj)
    {
        if (tagsObj is string single) return new[] { single };
        if (tagsObj is System.Collections.IList list)
            return list.Cast<object>().Select(o => o?.ToString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s));
        return Enumerable.Empty<string>();
    }

    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private bool IsActiveNote(string fullPath)
    {
        var relativePath = Path.GetRelativePath(_vaultPath, fullPath).Replace('\\', '/');
        return !relativePath.Equals(".trash", StringComparison.OrdinalIgnoreCase)
               && !relativePath.StartsWith(".trash/", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeLike(string input)
    {
        return input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }

    [GeneratedRegex(@"\[\[([^|\]]+)(?:\|[^\]]*)?\]\]", RegexOptions.Compiled)]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex MarkdownLinkRegex();
}

public class ObsidianLink
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

public class OrphanResult
{
    public string NotePath { get; set; } = string.Empty;
}

public class StalledResult
{
    public string NotePath { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
}

public class HistoryRecord
{
    public string NotePath { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string? FrontmatterJson { get; set; }
    public string ContentHash { get; set; } = string.Empty;
}
