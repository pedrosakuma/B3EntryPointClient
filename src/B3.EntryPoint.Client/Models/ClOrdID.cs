using System.Text.Json.Serialization;

namespace B3.EntryPoint.Client.Models;

/// <summary>
/// Client order ID (FIX <c>ClOrdID</c>). Wraps the wire <c>uint64</c>
/// (per schema <c>&lt;type name="ClOrdID" primitiveType="uint64"/&gt;</c>).
/// </summary>
[JsonConverter(typeof(ClOrdIDJsonConverter))]
public readonly record struct ClOrdID
{
    public ClOrdID(ulong value)
    {
        if (value == 0)
            throw new ArgumentException("ClOrdID cannot be zero (reserved as null sentinel).", nameof(value));
        Value = value;
    }

    public ulong Value { get; }

    /// <summary>Parses a numeric string into a ClOrdID.</summary>
    public static ClOrdID Parse(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new ClOrdID(ulong.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static implicit operator ulong(ClOrdID id) => id.Value;
    public static explicit operator ClOrdID(ulong value) => new(value);
}

