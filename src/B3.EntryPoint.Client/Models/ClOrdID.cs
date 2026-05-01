namespace B3.EntryPoint.Client.Models;

/// <summary>
/// Client order ID (FIX <c>ClOrdID</c>). Strongly-typed wrapper to keep the
/// public API distinct from arbitrary strings. Max 20 characters per spec.
/// </summary>
public readonly record struct ClOrdID
{
    public const int MaxLength = 20;

    public ClOrdID(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.Length > MaxLength)
            throw new ArgumentException(
                $"ClOrdID must be at most {MaxLength} characters (got {value.Length}).",
                nameof(value));
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static implicit operator string(ClOrdID id) => id.Value;
}
