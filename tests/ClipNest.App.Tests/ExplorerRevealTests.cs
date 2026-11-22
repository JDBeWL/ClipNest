using ClipNest.App.Services;

namespace ClipNest.App.Tests;

public sealed class ExplorerRevealTests
{
    [Theory]
    [InlineData("file:///D:/Photos", @"D:\Photos", true)]
    [InlineData("file:///D:/Photos/", @"D:\Photos", true)]
    [InlineData("file:///d:/photos", @"D:\Photos", true)]
    [InlineData("file:///D:/My%20Photos", @"D:\My Photos", true)]
    [InlineData("file:///D:/Photos", @"D:\Photos\Sub", false)]
    [InlineData("file:///D:/Photos", @"E:\Photos", false)]
    [InlineData("https://example.com/D:/Photos", @"D:\Photos", false)]
    [InlineData("file:///::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", @"D:\Photos", false)]
    [InlineData("", @"D:\Photos", false)]
    [InlineData(null, @"D:\Photos", false)]
    public void MatchesFolder_ComparesLocalPathIgnoringCaseAndTrailingSlash(
        string? url, string folder, bool expected)
    {
        Assert.Equal(expected, ExplorerReveal.MatchesFolder(url, folder));
    }
}