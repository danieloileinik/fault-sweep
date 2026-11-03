using System.Text;
using System.Text.Json;

namespace FaultSweep.Snapshots;

/// <summary>An immutable JSON snapshot. Store adapters must order rows by a stable key.</summary>
public sealed class StateSnapshot
{
    private StateSnapshot(JsonElement value)
    {
        Value = value.Clone();
    }

    public JsonElement Value { get; }

    public static StateSnapshot From<T>(T state, JsonSerializerOptions? options = null)
    {
        return new StateSnapshot(JsonSerializer.SerializeToElement(state, options));
    }

    public static StateSnapshot FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new StateSnapshot(document.RootElement);
    }

    public override string ToString() => Format(Value);

    internal static string Format(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
