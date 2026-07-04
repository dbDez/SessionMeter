using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionMeter.Core.Serialization;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for the SessionMeter core. Wire payloads (the OAuth usage
/// endpoint's response) are snake_case, so a single snake_case policy keeps the .NET side agreeing with the
/// endpoint's field names. Ported from MO's <c>MoJson</c> — the <see cref="Snake"/> options are what the
/// usage parse needs; <see cref="SnakePretty"/> is kept for symmetry with the source.
/// </summary>
public static class CoreJson
{
    /// <summary>Snake_case options used for parsing the usage wire contract.</summary>
    public static JsonSerializerOptions Snake { get; } = Build(indented: false);

    /// <summary>Indented variant for human-readable output.</summary>
    public static JsonSerializerOptions SnakePretty { get; } = Build(indented: true);

    private static JsonSerializerOptions Build(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = indented,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
