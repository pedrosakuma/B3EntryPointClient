using System.Buffers.Binary;

namespace B3.EntryPoint.Client.Framing;

/// <summary>
/// Writes a SOFH framing header into a buffer. See <see cref="SofhFrameReader"/>
/// for the wire layout.
/// </summary>
public static class SofhFrameWriter
{
    public const int HeaderSize = SofhFrameReader.HeaderSize;

    /// <summary>
    /// Writes a SOFH header at the beginning of <paramref name="buffer"/>.
    /// <paramref name="messageLength"/> must include the 4 header bytes.
    /// </summary>
    public static void WriteHeader(Span<byte> buffer, ushort messageLength, ushort encodingType = SofhFrameReader.SbeLittleEndianEncodingType)
    {
        if (buffer.Length < HeaderSize)
            throw new ArgumentException($"Buffer too small for SOFH header (need {HeaderSize}).", nameof(buffer));

        BinaryPrimitives.WriteUInt16LittleEndian(buffer, messageLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..], encodingType);
    }
}
