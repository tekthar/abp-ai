using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbpAi.Providers;

/// <summary>
/// Puts a call's provider options (<see cref="AiRequest.ProviderOptions"/>) into a provider's request body. What the
/// gateway set stays as it set it, and its own keys are never taken from the options.
/// </summary>
internal static class ProviderOptions
{
    /// <summary>
    /// Adds each key of the options that the body doesn't have, leaving out <paramref name="reserved"/>; where both hold an
    /// object under one key (<c>output_config</c> with a schema in it), the options' members it lacks are added, and so on
    /// down.
    /// </summary>
    public static void MergeInto(JsonObject body, JsonElement? options, IReadOnlyCollection<string> reserved)
    {
        if (options is not { ValueKind: JsonValueKind.Object } given)
        {
            return;
        }

        foreach (JsonProperty option in given.EnumerateObject())
        {
            if (!reserved.Contains(option.Name))
            {
                Merge(body, option.Name, JsonNode.Parse(option.Value.GetRawText()));
            }
        }
    }

    private static void Merge(JsonObject into, string key, JsonNode? value)
    {
        if (!into.TryGetPropertyValue(key, out JsonNode? there))
        {
            into[key] = value;
            return;
        }

        if (there is JsonObject existing && value is JsonObject more)
        {
            foreach ((string name, JsonNode? member) in more.ToList())
            {
                more.Remove(name);
                Merge(existing, name, member);
            }
        }
    }
}
