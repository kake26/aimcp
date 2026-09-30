public static class VaultSchema
{
    public static readonly string[] NoteTypes = { "idea", "question", "decision", "project", "person", "digest", "reference" };

    public static readonly Dictionary<string, string[]> RequiredFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["idea"] = new[] { "type", "status", "created" },
        ["question"] = new[] { "type", "status", "created" },
        ["decision"] = new[] { "type", "options", "chosen", "review_on" },
        ["project"] = new[] { "type", "status", "created" },
        ["person"] = new[] { "type", "name" },
        ["digest"] = new[] { "type", "period", "generated" },
        ["reference"] = new[] { "type", "source", "added" }
    };

    // Simple status state machines. Open-ended for types not defined yet.
    public static readonly Dictionary<string, string[]> StatusFlows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["idea"] = new[] { "seed", "exploring", "testing", "done", "abandoned" }
    };

    public static bool IsValidNoteType(string? type) =>
        !string.IsNullOrWhiteSpace(type) && NoteTypes.Contains(type, StringComparer.OrdinalIgnoreCase);

    public static bool IsValidStatusTransition(string noteType, string? fromStatus, string toStatus)
    {
        if (!StatusFlows.TryGetValue(noteType, out var flow))
            return true; // No defined machine: allow any transition.

        var normalizedTo = toStatus.ToLowerInvariant();
        if (!flow.Contains(normalizedTo))
            return false;

        if (string.IsNullOrWhiteSpace(fromStatus))
            return normalizedTo == flow[0]; // Must start at first state.

        var normalizedFrom = fromStatus.ToLowerInvariant();
        if (!flow.Contains(normalizedFrom))
            return false;

        var fromIndex = Array.IndexOf(flow, normalizedFrom);
        var toIndex = Array.IndexOf(flow, normalizedTo);

        // Allow forward moves, branching to abandoned from any state, or staying the same.
        if (normalizedTo == normalizedFrom) return true;
        if (normalizedTo == "abandoned") return true;
        return toIndex > fromIndex;
    }

    public static string[] ValidateFrontmatter(string? noteType, Dictionary<string, object> frontmatter)
    {
        var errors = new List<string>();
        if (!IsValidNoteType(noteType))
        {
            errors.Add($"Invalid or missing note type '{noteType}'. Valid types: {string.Join(", ", NoteTypes)}");
            return errors.ToArray();
        }

        var required = RequiredFields[noteType!];
        foreach (var field in required)
        {
            if (!frontmatter.ContainsKey(field))
                errors.Add($"Missing required field '{field}' for type '{noteType}'");
        }

        return errors.ToArray();
    }
}
