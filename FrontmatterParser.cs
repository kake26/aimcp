using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public static class FrontmatterParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static Dictionary<string, object>? Parse(string content)
    {
        if (!TryExtract(content, out var yaml, out _))
            return null;
        if (string.IsNullOrWhiteSpace(yaml))
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        var parsed = Deserializer.Deserialize<Dictionary<string, object>>(yaml);
        return parsed == null
            ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(parsed, StringComparer.OrdinalIgnoreCase);
    }

    public static string Update(string content, IReadOnlyDictionary<string, object> properties)
    {
        var existing = Parse(content) ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
            existing[property.Key] = property.Value;

        var frontmatter = $"---\n{Serializer.Serialize(existing)}---";
        if (!TryExtract(content, out _, out var bodyStart))
            return content.StartsWith('\n') ? frontmatter + content : frontmatter + "\n\n" + content;

        var body = content[bodyStart..];
        return body.StartsWith('\n') || body.StartsWith("\r\n", StringComparison.Ordinal)
            ? frontmatter + body
            : frontmatter + "\n" + body;
    }

    public static bool TryExtract(string content, out string yaml, out int bodyStart)
    {
        yaml = string.Empty;
        bodyStart = 0;
        if (!HasOpeningDelimiter(content, out var openingEnd))
            return false;

        var lineStart = openingEnd;
        while (lineStart < content.Length)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            var contentEnd = lineEnd >= 0 ? lineEnd : content.Length;
            if (contentEnd > lineStart && content[contentEnd - 1] == '\r')
                contentEnd--;

            if (content.AsSpan(lineStart, contentEnd - lineStart).SequenceEqual("---"))
            {
                yaml = content[openingEnd..lineStart].Trim();
                bodyStart = lineEnd >= 0 ? lineEnd : content.Length;
                return true;
            }

            if (lineEnd < 0)
                break;
            lineStart = lineEnd + 1;
        }

        return false;
    }

    private static bool HasOpeningDelimiter(string content, out int openingEnd)
    {
        openingEnd = 0;
        if (content.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            openingEnd = 5;
            return true;
        }
        if (content.StartsWith("---\n", StringComparison.Ordinal))
        {
            openingEnd = 4;
            return true;
        }
        if (content.Equals("---", StringComparison.Ordinal))
        {
            openingEnd = 3;
            return true;
        }
        return false;
    }
}
