using System.Buffers.Binary;

namespace B3.EntryPoint.Client.Framing;

/// <summary>
/// Simple Open Framing Header (SOFH) reader for B3 EntryPoint.
/// The B3 framing header is 4 bytes: <c>uint16 messageLength</c> followed by
/// <c>uint16 encodingType</c>, both in little-endian as defined by the SBE schema.
/// <para>
/// Note: <c>messageLength</c> is the total length of the framed message,
/// including the 4-byte framing header itself.
/// </para>
/// </summary>
public static class SofhFrameReader
{
    public const int HeaderSize = 4;

    /// <summary>
    /// Standard SBE 1.0 little-endian encoding type (FIX SOFH §3).
    /// </summary>
    public const ushort SbeLittleEndianEncodingType = 0xEB50;

    /// <summary>
    /// Attempts to parse a SOFH header from the start of <paramref name="buffer"/>.
    /// </summary>
    /// <returns><c>true</c> when at least <see cref="HeaderSize"/> bytes are available.</returns>
    public static bool TryParseHeader(ReadOnlySpan<byte> buffer, out ushort messageLength, out ushort encodingType)
    {
        if (buffer.Length < HeaderSize)
        {
            messageLength = 0;
            encodingType = 0;
            return false;
        }

        messageLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        encodingType = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..]);
        return true;
    }

    /// <summary>
    /// Reads a single complete SOFH-framed message from <paramref name="stream"/> into
    /// a freshly allocated buffer that contains the entire frame (header + payload).
    /// Throws <see cref="EndOfStreamException"/> if the stream ends mid-frame, and
    /// <see cref="InvalidDataException"/> if the framing header is malformed.
    /// </summary>
    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        if (!TryParseHeader(header, out var messageLength, out _))
            throw new InvalidDataException("SOFH header truncated.");

        if (messageLength < HeaderSize)
            throw new InvalidDataException($"SOFH messageLength {messageLength} smaller than header size {HeaderSize}.");

        var frame = new byte[messageLength];
        header.AsSpan().CopyTo(frame);
        await stream.ReadExactlyAsync(frame.AsMemory(HeaderSize, messageLength - HeaderSize), ct).ConfigureAwait(false);
        return frame;
    }
}
