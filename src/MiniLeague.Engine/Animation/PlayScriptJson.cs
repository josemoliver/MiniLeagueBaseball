using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniLeague.Engine.Animation;

/// <summary>JSON for play scripts: camelCase, enums as camelCase strings, nulls left out.</summary>
public static class PlayScriptJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(PlayScript script) => JsonSerializer.Serialize(script, Options);

    public static PlayScript? Deserialize(string json) => JsonSerializer.Deserialize<PlayScript>(json, Options);
}
