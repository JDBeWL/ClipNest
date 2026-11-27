using ClipNest.Ai.Tagging;
using ClipNest.Core.Tagging;
using SkiaSharp;
using Xunit.Abstractions;

namespace ClipNest.Ai.Tests;

public sealed class ClTaggerTests
{
    private readonly ITestOutputHelper _output;

    public ClTaggerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void LoadMetadata_ParsesIndexKeyedSchemaAndFillsSparseGaps()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                tempFile,
                """
                {
                  "0": { "tag": "general", "category": "Rating" },
                  "2": { "tag": "1girl", "category": "General" },
                  "4": { "tag": "elaina_(majo_no_tabitabi)", "category": "Character" },
                  "5": { "tag": "majo_no_tabitabi", "category": "Copyright" },
                  "6": { "tag": "artist_a", "category": "Artist" },
                  "7": { "tag": "highres", "category": "Meta" },
                  "8": { "tag": "masterpiece", "category": "Quality" },
                  "9": { "tag": "sdxl", "category": "Model" }
                }
                """);

            var (names, categories) = ClTagger.LoadMetadata(tempFile);

            Assert.Equal(10, names.Length);
            Assert.Equal(10, categories.Length);
            Assert.All(names, name => Assert.NotNull(name));
            Assert.All(categories, category => Assert.NotNull(category));

            Assert.Equal("general", names[0]);
            Assert.Equal("", categories[0]);
            Assert.Equal("", names[1]);
            Assert.Equal("", categories[1]);
            Assert.Equal("1girl", names[2]);
            Assert.Equal(AiTagCategory.General, categories[2]);
            Assert.Equal("elaina_(majo_no_tabitabi)", names[4]);
            Assert.Equal(AiTagCategory.Character, categories[4]);
            Assert.Equal("majo_no_tabitabi", names[5]);
            Assert.Equal(AiTagCategory.Series, categories[5]);
            Assert.Equal("artist_a", names[6]);
            Assert.Equal(AiTagCategory.Artist, categories[6]);
            Assert.Equal("", categories[7]);
            Assert.Equal("", categories[8]);
            Assert.Equal("", categories[9]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void LoadMetadata_SupportsSplitIdxToTagSchema()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                tempFile,
                """
                {
                  "idx_to_tag": { "0": "solo", "2": "hatsune_miku" },
                  "tag_to_category": { "solo": "General", "hatsune_miku": "Character" }
                }
                """);

            var (names, categories) = ClTagger.LoadMetadata(tempFile);

            Assert.Equal(3, names.Length);
            Assert.Equal("solo", names[0]);
            Assert.Equal(AiTagCategory.General, categories[0]);
            Assert.Equal("", names[1]);
            Assert.Equal("hatsune_miku", names[2]);
            Assert.Equal(AiTagCategory.Character, categories[2]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ToPixels_PadsWithWhiteAndOutputsNormalizedBgrNchw()
    {
        // 宽图 4×2 缩放到 4×4，上下各留 1 行白边，中间两行为纯红。
        using var bitmap = new SKBitmap(new SKImageInfo(4, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(255, 0, 0, 255));

        var pixels = ClTagger.ToPixels(bitmap, 4, SKColors.White);

        Assert.Equal(3 * 4 * 4, pixels.Length);

        // 白色补边按 (x/255-0.5)/0.5 归一化后三通道均为 1。
        AssertBgrNchwAt(pixels, width: 4, x: 1, y: 0, 1f, 1f, 1f);
        AssertBgrNchwAt(pixels, width: 4, x: 2, y: 3, 1f, 1f, 1f);

        // 纯红像素转 BGR 后为 0、0、255，按同规则归一化为 -1、-1、+1。
        AssertBgrNchwAt(pixels, width: 4, x: 1, y: 1, -1f, -1f, 1f);
        AssertBgrNchwAt(pixels, width: 4, x: 2, y: 2, -1f, -1f, 1f);
    }

    [Fact]
    public void ToPixels_CompositesTransparentPixelsOntoWhite()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.SetPixel(0, 0, new SKColor(0, 0, 0, 0));
        bitmap.SetPixel(1, 0, new SKColor(0, 0, 255, 255));
        bitmap.SetPixel(0, 1, new SKColor(0, 255, 0, 255));
        bitmap.SetPixel(1, 1, new SKColor(255, 0, 0, 255));

        var pixels = ClTagger.ToPixels(bitmap, 2, SKColors.White);

        AssertBgrNchwAt(pixels, width: 2, x: 0, y: 0, 1f, 1f, 1f);
        AssertBgrNchwAt(pixels, width: 2, x: 1, y: 0, 1f, -1f, -1f);
        AssertBgrNchwAt(pixels, width: 2, x: 0, y: 1, -1f, 1f, -1f);
        AssertBgrNchwAt(pixels, width: 2, x: 1, y: 1, -1f, -1f, 1f);
    }

    [Fact]
    public async Task RecognizesSampleImage_WhenConfigured()
    {
        var modelDir = Environment.GetEnvironmentVariable("CLIPNEST_CL_TAGGER_DIR");
        var image = Environment.GetEnvironmentVariable("CLIPNEST_TEST_IMAGE");
        if (string.IsNullOrEmpty(modelDir) || string.IsNullOrEmpty(image))
        {
            _output.WriteLine("跳过：未设置 CLIPNEST_CL_TAGGER_DIR / CLIPNEST_TEST_IMAGE");
            return;
        }

        var manager = new TagModelManager(modelDir, TagModelDefinition.ClTagger);
        using var tagger = new ClTagger(manager);

        var result = await tagger.TagAsync(image);
        foreach (var tag in result.Tags)
        {
            _output.WriteLine($"{tag.Category} {tag.Name} {tag.Score:F3}");
        }

        Assert.NotEmpty(result.Tags);
    }

    private static void AssertBgrNchwAt(
        float[] pixels, int width, int x, int y, float expectedB, float expectedG, float expectedR)
    {
        var plane = width * width;
        var index = y * width + x;
        Assert.InRange(pixels[index], expectedB - 0.01f, expectedB + 0.01f);
        Assert.InRange(pixels[plane + index], expectedG - 0.01f, expectedG + 0.01f);
        Assert.InRange(pixels[2 * plane + index], expectedR - 0.01f, expectedR + 0.01f);
    }
}
