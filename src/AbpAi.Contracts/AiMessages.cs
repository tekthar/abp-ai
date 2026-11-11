using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbpAi;

/// <summary>Builds and reads messages in the Messages API's shape.</summary>
public static class AiMessages
{
    /// <summary>A conversation of one user message.</summary>
    public static JsonElement User(string text) => JsonSerializer.SerializeToElement(new[] { new { role = "user", content = text } });

    /// <summary>An assistant message of one text block.</summary>
    public static JsonElement AssistantText(string text, string stopReason = "end_turn") =>
        JsonSerializer.SerializeToElement(new { role = "assistant", content = new[] { new { type = "text", text } }, stop_reason = stopReason });

    /// <summary>An assistant message asking for one tool.</summary>
    public static JsonElement AssistantToolUse(string name, object input, string? id = null, string? text = null)
    {
        JsonArray content = [];
        if (!string.IsNullOrEmpty(text))
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        content.Add(new JsonObject
        {
            ["type"] = "tool_use", ["id"] = id ?? "toolu_" + Guid.NewGuid().ToString("N")[..20], ["name"] = name,
            ["input"] = JsonSerializer.SerializeToNode(input),
        });
        return JsonSerializer.SerializeToElement(new JsonObject { ["role"] = "assistant", ["content"] = content, ["stop_reason"] = "tool_use" });
    }

    /// <summary>The text of a message: its content when it's a string, else its text blocks joined.</summary>
    public static string TextOf(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out JsonElement content))
        {
            return "";
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }

        StringBuilder text = new();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement type) && type.GetString() == "text" && block.TryGetProperty("text", out JsonElement t))
                {
                    text.Append(t.GetString());
                }
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// A call's system text: the text of its system blocks (a string, or the Messages API's blocks), joined by a blank line,
    /// when it has blocks; else its system text. Null when it has neither.
    /// </summary>
    public static string? SystemText(string? system, JsonElement? blocks)
    {
        if (blocks is { ValueKind: JsonValueKind.String } text)
        {
            return text.GetString();
        }

        if (blocks is not { ValueKind: JsonValueKind.Array } array)
        {
            return system;
        }

        return string.Join("\n\n", array.EnumerateArray()
            .Where(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String)
            .Select(b => b.GetProperty("text").GetString()));
    }

    /// <summary>
    /// The text of the conversation's last user message that has text (one holding only tool results is passed over),
    /// for responders that look at what was asked.
    /// </summary>
    public static string LastUserText(JsonElement messages)
    {
        if (messages.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        string last = "";
        foreach (JsonElement message in messages.EnumerateArray())
        {
            if (message.TryGetProperty("role", out JsonElement role) && role.GetString() == "user" && TextOf(message) is { Length: > 0 } text)
            {
                last = text;
            }
        }

        return last;
    }

    /// <summary>The conversation holds an image: an <c>image</c> block in a message, or in a tool's result.</summary>
    public static bool HasImages(JsonElement messages)
    {
        if (messages.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement message in messages.EnumerateArray())
        {
            if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out JsonElement content) && HasImageBlock(content))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasImageBlock(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out JsonElement type)
                && (type.ValueEquals("image") || (type.ValueEquals("tool_result") && block.TryGetProperty("content", out JsonElement inner) && HasImageBlock(inner))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text as JSON, when it is (a fenced block's inside too); else null.</summary>
    public static JsonElement? TryParseJson(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            int start = trimmed.IndexOf('\n');
            int end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            trimmed = start > 0 && end > start ? trimmed[(start + 1)..end].Trim() : trimmed;
        }

        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(trimmed);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
