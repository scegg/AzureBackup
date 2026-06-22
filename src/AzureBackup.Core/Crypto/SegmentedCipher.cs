using System.Buffers.Binary;

namespace AzureBackup.Core.Crypto;

/// <summary>
/// Streaming AEAD for large pack data: AES-256-GCM applied per fixed-size segment,
/// each with its own nonce + tag. The per-segment associated data binds the
/// segment index and an "is-final" flag, so reordering, dropping or truncating
/// segments is detected on decrypt.
///
/// On-stream layout:
///   magic(4) "ABSC" | version(1) | segmentSize(int32 LE)
///   then per segment: flag(1) | sealedLen(int32 LE) | sealed( nonce|ct|tag )
/// where the segment AAD = index(int64 LE) || flag.
/// </summary>
public static class SegmentedCipher
{
    private static readonly byte[] Magic = "ABSC"u8.ToArray();
    private const byte Version = 1;
    public const int DefaultSegmentSize = 1 << 20; // 1 MiB

    public static void Encrypt(ReadOnlySpan<byte> key, Stream plaintext, Stream output, int segmentSize = DefaultSegmentSize)
    {
        if (segmentSize <= 0) throw new ArgumentOutOfRangeException(nameof(segmentSize));
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(output);

        output.Write(Magic);
        output.WriteByte(Version);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, segmentSize);
        output.Write(header);

        byte[] cur = new byte[segmentSize];
        byte[] nxt = new byte[segmentSize];
        int curLen = ReadFull(plaintext, cur);
        long index = 0;

        if (curLen == 0)
        {
            WriteSegment(output, key, index, isFinal: true, ReadOnlySpan<byte>.Empty);
            return;
        }

        while (true)
        {
            int nxtLen = ReadFull(plaintext, nxt);
            bool isFinal = nxtLen == 0;
            WriteSegment(output, key, index, isFinal, cur.AsSpan(0, curLen));
            index++;
            if (isFinal) break;
            (cur, nxt) = (nxt, cur);
            curLen = nxtLen;
        }
    }

    public static void Decrypt(ReadOnlySpan<byte> key, Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        Span<byte> magic = stackalloc byte[4];
        if (ReadFull(input, magic) != 4 || !magic.SequenceEqual(Magic))
            throw new InvalidDataException("bad segmented-cipher header");
        int version = input.ReadByte();
        if (version != Version)
            throw new InvalidDataException($"unsupported segmented-cipher version {version}");
        Span<byte> sizeBuf = stackalloc byte[4];
        if (ReadFull(input, sizeBuf) != 4)
            throw new InvalidDataException("truncated header");
        int segmentSize = BinaryPrimitives.ReadInt32LittleEndian(sizeBuf);
        if (segmentSize <= 0)
            throw new InvalidDataException("invalid segment size");

        int maxSealed = segmentSize + Aead.NonceBytes + Aead.TagBytes + 16;
        long index = 0;
        Span<byte> aad = stackalloc byte[9];
        Span<byte> lenBuf = stackalloc byte[4];

        while (true)
        {
            int flag = input.ReadByte();
            if (flag < 0)
                throw new InvalidDataException("truncated: stream ended before final segment");
            if (ReadFull(input, lenBuf) != 4)
                throw new InvalidDataException("truncated segment length");
            int sealedLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (sealedLen < Aead.NonceBytes + Aead.TagBytes || sealedLen > maxSealed)
                throw new InvalidDataException("invalid segment length");

            byte[] sealedBlob = new byte[sealedLen];
            if (ReadFull(input, sealedBlob) != sealedLen)
                throw new InvalidDataException("truncated segment body");

            BinaryPrimitives.WriteInt64LittleEndian(aad, index);
            aad[8] = (byte)flag;
            byte[] plaintext = Aead.Open(key, sealedBlob, aad); // authenticates index + flag
            output.Write(plaintext, 0, plaintext.Length);

            index++;
            if (flag == 1) break; // final segment
        }
    }

    private static void WriteSegment(Stream output, ReadOnlySpan<byte> key, long index, bool isFinal, ReadOnlySpan<byte> plaintext)
    {
        byte flag = isFinal ? (byte)1 : (byte)0;
        Span<byte> aad = stackalloc byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(aad, index);
        aad[8] = flag;

        byte[] sealedBlob = Aead.Seal(key, plaintext, aad);
        output.WriteByte(flag);
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, sealedBlob.Length);
        output.Write(lenBuf);
        output.Write(sealedBlob);
    }

    /// <summary>Reads until the buffer is full or the stream ends; returns bytes read.</summary>
    private static int ReadFull(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
