using System.Security.Cryptography;
using AzureBackup.Core.Volumes;
using Xunit;

namespace AzureBackup.Core.Tests;

public class VolumeSplitterTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 100)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]   // exact multiple
    [InlineData(250, 100)]   // remainder
    [InlineData(1000, 64)]
    public void Split_then_Join_roundtrips(int dataLen, int volumeSize)
    {
        byte[] data = RandomNumberGenerator.GetBytes(dataLen);

        IReadOnlyList<byte[]> volumes = VolumeSplitter.Split(data, volumeSize);
        byte[] rejoined = VolumeSplitter.Join(volumes);

        Assert.Equal(data, rejoined);
    }

    [Fact]
    public void Volumes_respect_size_and_count()
    {
        byte[] data = RandomNumberGenerator.GetBytes(250);
        IReadOnlyList<byte[]> volumes = VolumeSplitter.Split(data, 100);

        Assert.Equal(3, volumes.Count);
        Assert.Equal(100, volumes[0].Length);
        Assert.Equal(100, volumes[1].Length);
        Assert.Equal(50, volumes[2].Length);
    }

    [Fact]
    public void Empty_payload_yields_one_empty_volume()
    {
        IReadOnlyList<byte[]> volumes = VolumeSplitter.Split(ReadOnlySpan<byte>.Empty, 100);
        Assert.Single(volumes);
        Assert.Empty(volumes[0]);
    }

    [Fact]
    public void Non_positive_volume_size_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VolumeSplitter.Split(new byte[10], 0));
    }
}
