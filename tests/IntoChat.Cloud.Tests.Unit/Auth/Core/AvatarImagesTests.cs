namespace IntoChat.Cloud.Auth.Core.Tests;

public sealed class AvatarImagesTests
{
    [Fact]
    public void Detect_RecognisesPngAndJpegByLeadingBytes()
    {
        Assert.Equal(AvatarImages.Png, AvatarImages.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Equal(AvatarImages.Jpeg, AvatarImages.Detect([0xFF, 0xD8, 0xFF, 0xE1]));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 })]
    public void Detect_RejectsEverythingElse(byte[] bytes) => Assert.Null(AvatarImages.Detect(bytes));
}
