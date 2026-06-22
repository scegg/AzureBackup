using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureBackup.Core.Serialization;

/// <summary>Canonical JSON settings for repository structure files
/// (serialized, then compressed + encrypted before upload).</summary>
public static class RepoJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8)
        => JsonSerializer.Deserialize<T>(utf8, Options)
           ?? throw new InvalidDataException($"deserialized null for {typeof(T).Name}");
}
