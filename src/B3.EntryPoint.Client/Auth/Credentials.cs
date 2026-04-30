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

    public Credentials(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 255)
            throw new ArgumentException("Credentials must fit in a single SBE varData byte length (≤ 255).", nameof(bytes));
        _bytes = bytes.ToArray();
    }

    public static Credentials FromUtf8(string accessKey)
    {
        ArgumentNullException.ThrowIfNull(accessKey);
        return new Credentials(System.Text.Encoding.UTF8.GetBytes(accessKey));
    }

    public ReadOnlySpan<byte> AsSpan() => _bytes;
}
