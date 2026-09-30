using System.Text.Json;

public static class VaultCli
{
    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: aimcp <command> [args]");
            Console.WriteLine("Commands: write, append, move, delete, transition, search, tags, links-to, orphans, stalled, history, validate, digest");
            return 1;
        }

        var kernel = services.GetRequiredService<VaultKernel>();
        var index = services.GetRequiredService<VaultIndex>();
        var command = args[0].ToLowerInvariant();

        switch (command)
        {
            case "write":
                {
                    if (args.Length < 3) { Console.WriteLine("Usage: write <path> <content>"); return 1; }
                    var result = await kernel.UpdateNoteAsync(args[1], string.Join(" ", args[2..]));
                    return PrintResult(result);
                }
            case "append":
                {
                    if (args.Length < 3) { Console.WriteLine("Usage: append <path> <content>"); return 1; }
                    var result = await kernel.AppendNoteAsync(args[1], string.Join(" ", args[2..]));
                    return PrintResult(result);
                }
            case "move":
                {
                    if (args.Length < 3) { Console.WriteLine("Usage: move <source> <destination>"); return 1; }
                    var result = await kernel.TransitionNoteAsync(args[1], args[2]);
                    return PrintResult(result);
                }
            case "delete":
                {
                    if (args.Length < 2) { Console.WriteLine("Usage: delete <path>"); return 1; }
                    var result = await kernel.DeleteNoteAsync(args[1]);
                    return PrintResult(result);
                }
            case "transition":
                {
                    if (args.Length < 3) { Console.WriteLine("Usage: transition <path> <status>"); return 1; }
                    var result = await kernel.TransitionStatusAsync(args[1], args[2]);
                    return PrintResult(result);
                }
            case "search":
                {
                    if (args.Length < 2) { Console.WriteLine("Usage: search <query>"); return 1; }
                    var results = index.SearchNotes(args[1]);
                    Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
            case "tags":
                {
                    var tags = index.ListAllTags();
                    Console.WriteLine(JsonSerializer.Serialize(tags, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
            case "links-to":
                {
                    if (args.Length < 2) { Console.WriteLine("Usage: links-to <path>"); return 1; }
                    var results = index.GetLinksTo(args[1]);
                    Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
            case "orphans":
                {
                    var results = index.GetOrphans();
                    Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
            case "stalled":
                {
                    var days = args.Length > 1 && int.TryParse(args[1], out var d) ? d : 30;
                    var results = index.GetStalled(days);
                    Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
            case "history":
                {
                    if (args.Length < 2) { Console.WriteLine("Usage: history <path>"); return 1; }
                    var limit = args.Length > 2 && int.TryParse(args[2], out var l) ? l : 50;
                    var results = index.QueryHistory(args[1], limit);
                    Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
            case "validate":
                {
                    var paths = string.IsNullOrWhiteSpace(args.Length > 1 ? args[1] : "")
                        ? index.ListNotes()
                        : new List<string> { args[1] };
                    var errors = new List<string>();
                    foreach (var path in paths)
                    {
                        var content = index.ReadNoteContent(path);
                        if (content == null) continue;
                        var props = FrontmatterParser.Parse(content);
                        if (props == null) { errors.Add($"{path}: no frontmatter"); continue; }
                        var type = props.TryGetValue("type", out var t) ? t?.ToString() : null;
                        var validationErrors = VaultSchema.ValidateFrontmatter(type, props);
                        if (validationErrors.Length > 0)
                            errors.Add($"{path}: {string.Join("; ", validationErrors)}");
                    }
                    if (errors.Count == 0)
                    {
                        Console.WriteLine("All notes pass schema validation.");
                        return 0;
                    }
                    Console.WriteLine(string.Join("\n", errors));
                    return 1;
                }
            case "digest":
                {
                    var days = args.Length > 1 && int.TryParse(args[1], out var d) ? d : 7;
                    var since = DateTime.UtcNow.AddDays(-days).Ticks;
                    var recent = index.QueryRecentHistory(since);
                    var orphans = index.GetOrphans();
                    var stalled = index.GetStalled(30);
                    Console.WriteLine($"# Vault Digest (last {days} days)");
                    Console.WriteLine($"Recent operations: {recent.Count}");
                    foreach (var r in recent.Take(20))
                        Console.WriteLine($"- {r.ChangedAt:u} {r.Operation} {r.NotePath}");
                    Console.WriteLine($"Orphans: {orphans.Count}");
                    foreach (var o in orphans.Take(20))
                        Console.WriteLine($"- {o.NotePath}");
                    Console.WriteLine($"Stalled (>30d): {stalled.Count}");
                    foreach (var s in stalled.Take(20))
                        Console.WriteLine($"- {s.NotePath} (last {s.LastModified:u})");
                    return 0;
                }
            default:
                Console.WriteLine($"Unknown command: {command}");
                return 1;
        }
    }

    private static int PrintResult(VaultCommandResult result)
    {
        if (result.Success)
        {
            Console.WriteLine(result.Message);
            return 0;
        }
        Console.Error.WriteLine(result.Error ?? "Command failed");
        return 1;
    }
}
