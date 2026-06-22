using System.Text;
using AzureBackup.Core.Compression;
using Xunit;

namespace AzureBackup.Core.Tests;

public class CompressionTests
{
    private static byte[] RoundTrip(ICompressor codec, byte[] data)
    {
        using var compressed = new MemoryStream();
        codec.Compress(new MemoryStream(data), compressed);

        compressed.Position = 0;
        using var restored = new MemoryStream();
        codec.Decompress(compressed, restored);
        return restored.ToArray();
    }

    [Fact]
    public void Store_roundtrips_unchanged()
    {
        byte[] data = Encoding.UTF8.GetBytes("incompressible-ish payload 内容");
        var codec = new StoreCompressor();

        using var compressed = new MemoryStream();
        codec.Compress(new MemoryStream(data), compressed);

        Assert.Equal(data, compressed.ToArray()); // identity
        Assert.Equal(data, RoundTrip(codec, data));
    }

    [Fact]
    public void Xz_roundtrips_and_shrinks_compressible_data()
    {
        if (!XzCompressor.IsAvailable())
            return; // xz not on PATH (skip); CI image provides it

        byte[] data = Encoding.UTF8.GetBytes(new string('a', 100_000));
        var codec = new XzCompressor();

        using var compressed = new MemoryStream();
        codec.Compress(new MemoryStream(data), compressed);

        Assert.True(compressed.Length < data.Length / 10, "highly compressible data should shrink a lot");
        Assert.Equal(data, RoundTrip(codec, data));
    }

    [Fact]
    public void Xz_roundtrips_binary_data()
    {
        if (!XzCompressor.IsAvailable())
            return;

        byte[] data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(50_000);
        Assert.Equal(data, RoundTrip(new XzCompressor(), data));
    }

    [Fact]
    public void Factory_resolves_codecs()
    {
        Assert.IsType<StoreCompressor>(Compressors.For(CompressionCodec.Store));
        Assert.IsType<XzCompressor>(Compressors.For(CompressionCodec.Xz));
    }
}
