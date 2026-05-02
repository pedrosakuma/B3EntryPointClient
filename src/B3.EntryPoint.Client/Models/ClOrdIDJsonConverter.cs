using System.Text.Json;
using System.Text.Json.Serialization;

namespace B3.EntryPoint.Client.Models;

/// <summary>
/// Serializes <see cref="ClOrdID"/> as a JSON number (its underlying
/// <see cref="ClOrdID.Value"/>). For backward compatibility with persisted
/// state written by &lt;= v0.13.0 (where <c>OrderClosedDelta.ClOrdID</c> was
/// a <see cref="string"/>), the reader also accepts a JSON string containing
/// a base-10 unsigned integer. (#128)
/// </summary>
public sealed class ClOrdIDJsonConverter : JsonConverter<ClOrdID>
{
    public override ClOrdID Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return new ClOrdID(reader.GetUInt64());
            case JsonTokenType.String:
                {
                    var s = reader.GetString();
                    if (string.IsNullOrEmpty(s))
                        throw new JsonException("ClOrdID string was null or empty.");
                    return ClOrdID.Parse(s);
                }
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} when reading ClOrdID.");
        }
    }

    public override void Write(Utf8JsonWriter writer, ClOrdID value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);
}
