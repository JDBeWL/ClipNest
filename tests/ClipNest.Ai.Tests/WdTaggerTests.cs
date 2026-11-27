using ClipNest.Ai.Tagging;
using ClipNest.Core.Tagging;
using SkiaSharp;
using Xunit.Abstractions;

namespace ClipNest.Ai.Tests;

public sealed class WdTaggerTests
{
    private readonly ITestOutputHelper _output;

    public WdTaggerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void LoadLabels_ParsesDanbooruCategoriesAndQuotedFields()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                tempFile,
                "tag_id,name,category,count\r\n"
                + "9999999,general,9,4500000\r\n"
                + "470575,1girl,0,4200000\r\n"
                + "1234,\"quoted,tag\",0,500\r\n"
                + "5678,some_artist,1,200\r\n"
                + "9012,majo_no_tabitabi,3,3000\r\n"
                + "3456,elaina_(majo_no_tabitabi),4,2500\r\n"
                + "\r\n");

            var (names, categories) = WdTagger.LoadLabels(tempFile);

            Assert.Equal(6, names.Length);
            Assert.Equal(
                new[]
                {
                    "general",
                    "1girl",
                    "quoted,tag",
                    "some_artist",
                    "majo_no_tabitabi",
                    "elaina_(majo_no_tabitabi)"
                },
                names);
            Assert.Equal(
                new[]
                {
                    "",
                    AiTagCategory.General,
                    AiTagCategory.General,
                    AiTagCategory.Artist,
                    AiTagCategory.Series,
                    AiTagCategory.Character
                },
                categories);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ToPixels_PadsWithWhiteAndOutputsBgrNhwcIn255Range()
    {
        // 宽图 4×2 缩放到 4×4，上下各留 1 行白边，中间两行为纯红。
        using var bitmap = new SKBitmap(new SKImageInfo(4, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(255, 0, 0, 255));

        var pixels = WdTagger.ToPixels(bitmap, 4, SKColors.White);

        Assert.Equal(4 * 4 * 3, pixels.Length);

        // 第 0 行与第 3 行为白色补边，BGR 均为 255。
        AssertBgrAt(pixels, width: 4, x: 1, y: 0, 255f, 255f, 255f);
        AssertBgrAt(pixels, width: 4, x: 2, y: 3, 255f, 255f, 255f);

        // 中间两行为纯红像素，按 BGR 排列应为 0、0、255。
        AssertBgrAt(pixels, width: 4, x: 1, y: 1, 0f, 0f, 255f);
        AssertBgrAt(pixels, width: 4, x: 2, y: 2, 0f, 0f, 255f);
    }

    [Fact]
    public void ToPixels_CompositesTransparentPixelsOntoWhite()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.SetPixel(0, 0, new SKColor(0, 0, 0, 0));
        bitmap.SetPixel(1, 0, new SKColor(0, 0, 255, 255));
        bitmap.SetPixel(0, 1, new SKColor(0, 0, 0, 128));
        bitmap.SetPixel(1, 1, new SKColor(0, 255, 0, 255));

        var pixels = WdTagger.ToPixels(bitmap, 2, SKColors.White);

        AssertBgrAt(pixels, width: 2, x: 0, y: 0, 255f, 255f, 255f);
        AssertBgrAt(pixels, width: 2, x: 1, y: 0, 255f, 0f, 0f);
        AssertBgrAt(pixels, width: 2, x: 1, y: 1, 0f, 255f, 0f);

        // 半透明黑色叠加白底后约等于中灰。
        var idx = (1 * 2 + 0) * 3;
        Assert.InRange(pixels[idx], 120f, 136f);
        Assert.InRange(pixels[idx + 1], 120f, 136f);
        Assert.InRange(pixels[idx + 2], 120f, 136f);
    }

    [Fact]
    public void Definitions_HaveUniqueIdsAndValidFiles()
    {
        var all = TagModelDefinition.All;
        Assert.Equal(all.Count, all.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Same(TagModelDefinition.CamieV2, TagModelDefinition.Default);
        Assert.Contains(TagModelDefinition.WdEva02LargeV3, all);
        Assert.Contains(TagModelDefinition.WdSwinV2V3, all);
        Assert.Contains(TagModelDefinition.ClTagger, all);

        foreach (var definition in all)
        {
            Assert.NotEmpty(definition.BaseUrls);
            Assert.NotEmpty(definition.Files);
            Assert.All(definition.Files, file => Assert.True(file.ExpectedBytes > 0));
        }

        foreach (var wd in new[] { TagModelDefinition.WdEva02LargeV3, TagModelDefinition.WdSwinV2V3 })
        {
            Assert.Equal(TagModelKind.WdTagger, wd.Kind);
            Assert.Equal(448, wd.ImageSize);
            Assert.Equal(255, wd.PadRed);
            Assert.Equal(255, wd.PadGreen);
            Assert.Equal(255, wd.PadBlue);
        }
    }

    [Fact]
    public async Task RecognizesSampleImage_WhenConfigured()
    {
        var modelDir = Environment.GetEnvironmentVariable("CLIPNEST_WD_V3_DIR");
        var image = Environment.GetEnvironmentVariable("CLIPNEST_TEST_IMAGE");
        if (string.IsNullOrEmpty(modelDir) || string.IsNullOrEmpty(image))
        {
            _output.WriteLine("跳过：未设置 CLIPNEST_WD_V3_DIR / CLIPNEST_TEST_IMAGE");
            return;
        }

        var definition = Environment.GetEnvironmentVariable("CLIPNEST_WD_V3_VARIANT") == "eva02"
            ? TagModelDefinition.WdEva02LargeV3
            : TagModelDefinition.WdSwinV2V3;
        var manager = new TagModelManager(modelDir, definition);
        using var tagger = new WdTagger(manager);

        var result = await tagger.TagAsync(image);
        foreach (var tag in result.Tags)
        {
            _output.WriteLine($"{tag.Category} {tag.Name} {tag.Score:F3}");
        }

        Assert.NotEmpty(result.Tags);
    }

    private static void AssertBgrAt(
        float[] pixels, int width, int x, int y, float expectedB, float expectedG, float expectedR)
    {
        var offset = (y * width + x) * 3;
        Assert.InRange(pixels[offset], expectedB - 1f, expectedB + 1f);
        Assert.InRange(pixels[offset + 1], expectedG - 1f, expectedG + 1f);
        Assert.InRange(pixels[offset + 2], expectedR - 1f, expectedR + 1f);
    }
}
