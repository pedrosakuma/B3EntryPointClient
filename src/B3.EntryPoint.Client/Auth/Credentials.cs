namespace B3.EntryPoint.Client.Auth;

/// <summary>
/// Opaque authentication payload sent in the <c>Credentials</c> variable-length
/// field of <c>Negotiate</c> and <c>Establish</c>. The B3 spec does not constrain
/// the byte layout of <c>Credentials</c> — it is a deployment-specific token
/// (typically a UTF-8 access key in the simulator, or an HSM-issued blob in UAT).
/// </summary>
public sealed class Credentials
{
    private readonly byte[] _bytes;

    /// <summary>
    /// Schema-defined upper bound on <c>CredentialsEncoding.length</c>
    /// (schema 8.4.2 §720, <c>uint8 maxValue="128"</c>). The gateway will
    /// reject Establish/Negotiate with a longer Credentials field.
    /// </summary>
    public const int MaxLengthBytes = 128;

    public Credentials(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxLengthBytes)
            throw new ArgumentException(
                $"Credentials must be ≤ {MaxLengthBytes} bytes per schema CredentialsEncoding.length maxValue.",
                nameof(bytes));
        _bytes = bytes.ToArray();
    }

    public static Credentials FromUtf8(string accessKey)
    {
        ArgumentNullException.ThrowIfNull(accessKey);
        return new Credentials(System.Text.Encoding.UTF8.GetBytes(accessKey));
    }

    public ReadOnlySpan<byte> AsSpan() => _bytes;
}
