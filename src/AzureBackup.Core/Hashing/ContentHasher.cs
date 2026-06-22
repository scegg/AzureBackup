namespace AzureBackup.Core.Hashing;

/// <summary>
/// BLAKE3 content hashing — used for dedup, change detection and verify.
/// The algorithm is fixed at repo init and recorded in <c>config.hashAlgo</c>.
/// </summary>
public static class ContentHasher
{
    public const string AlgoName = "blake3";
    public const int HashBytes = 32;

    public static byte[] Hash(ReadOnlySpan<byte> data)
        => Blake3.Hasher.Hash(data).AsSpan().ToArray();

    public static byte[] HashStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var hasher = Blake3.Hasher.New();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hasher.Update(buffer.AsSpan(0, read));
        return hasher.Finalize().AsSpan().ToArray();
    }

    public static string ToHex(ReadOnlySpan<byte> hash) => Convert.ToHexStringLower(hash);
}
