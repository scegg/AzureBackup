namespace AzureBackup.Core.Volumes;

/// <summary>
/// Splits a pack's "compress-then-encrypt" byte stream into fixed-size volumes,
/// and rejoins them. Volumes are plain ciphertext chunks — forward-only, never
/// edited. A pack's volumes are only admitted/uploaded once the whole pack succeeds.
/// </summary>
public static class VolumeSplitter
{
    public static IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> data, int volumeSize)
    {
        if (volumeSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(volumeSize), "volume size must be positive");

        var volumes = new List<byte[]>();
        for (int offset = 0; offset < data.Length; offset += volumeSize)
        {
            int len = Math.Min(volumeSize, data.Length - offset);
            volumes.Add(data.Slice(offset, len).ToArray());
        }
        if (volumes.Count == 0)
            volumes.Add([]); // empty payload still yields exactly one (empty) volume
        return volumes;
    }

    public static byte[] Join(IEnumerable<byte[]> volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        using var ms = new MemoryStream();
        foreach (byte[] v in volumes)
            ms.Write(v, 0, v.Length);
        return ms.ToArray();
    }
}
