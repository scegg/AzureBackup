using System.Text;
using AzureBackup.Core.Hashing;
using Xunit;

namespace AzureBackup.Core.Tests;

public class ContentHasherTests
{
    // Official BLAKE3 test vector for empty input.
    private const string EmptyHashHex = "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262";

    [Fact]
    public void Empty_input_matches_known_vector()
    {
        byte[] hash = ContentHasher.Hash(ReadOnlySpan<byte>.Empty);
        Assert.Equal(ContentHasher.HashBytes, hash.Length);
        Assert.Equal(EmptyHashHex, ContentHasher.ToHex(hash));
    }

    [Fact]
    public void Same_input_same_hash_and_different_input_differs()
    {
        byte[] a = ContentHasher.Hash("the quick brown fox"u8);
        byte[] b = ContentHasher.Hash("the quick brown fox"u8);
        byte[] c = ContentHasher.Hash("the quick brown FOX"u8);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Stream_hash_equals_buffer_hash()
    {
        byte[] data = Encoding.UTF8.GetBytes(new string('x', 200_000));
        byte[] buffered = ContentHasher.Hash(data);

        using var ms = new MemoryStream(data);
        byte[] streamed = ContentHasher.HashStream(ms);

        Assert.Equal(buffered, streamed);
    }
}
