using ClipNest.Imaging.Decoding;

namespace ClipNest.Imaging.Tests;

public sealed class LibRawImageDecoderTests
{
    [Fact]
    public void CanDecode_RawExtensions_ReturnsTrue()
    {
        var decoder = new LibRawImageDecoder();
        Assert.True(decoder.CanDecode(".cr2"));
        Assert.True(decoder.CanDecode(".nef"));
        Assert.True(decoder.CanDecode(".arw"));
        Assert.False(decoder.CanDecode(".jpg"));
    }

    [Fact]
    public async Task Decode_MissingFile_ReturnsNull()
    {
        Assert.Null(await new LibRawImageDecoder().DecodeAsync("missing.cr2", 512));
    }
}
