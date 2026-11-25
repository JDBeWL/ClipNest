using ClipNest.Ai.Tagging;
using Xunit.Abstractions;

namespace ClipNest.Ai.Tests;

/// <summary>
/// SigLIP2 的真实权重冒烟测试。设置 CLIPNEST_SIGLIP_DIR、CLIPNEST_SIGLIP_ASSETS
/// 与 CLIPNEST_TEST_IMAGE 后运行；未设置时跳过，不参与常规测试。
/// </summary>
public sealed class SiglipSmokeTests
{
    private readonly ITestOutputHelper _output;

    public SiglipSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RecognizesSampleImage()
    {
        var modelDir = Environment.GetEnvironmentVariable("CLIPNEST_SIGLIP_DIR");
        var assets = Environment.GetEnvironmentVariable("CLIPNEST_SIGLIP_ASSETS");
        var image = Environment.GetEnvironmentVariable("CLIPNEST_TEST_IMAGE");
        if (string.IsNullOrEmpty(modelDir) || string.IsNullOrEmpty(assets)
            || string.IsNullOrEmpty(image))
        {
            _output.WriteLine("跳过：未设置 CLIPNEST_SIGLIP_DIR / CLIPNEST_SIGLIP_ASSETS / CLIPNEST_TEST_IMAGE");
            return;
        }

        var manager = new TagModelManager(modelDir, TagModelDefinition.Siglip2);
        using var tagger = new SiglipTagger(manager, assets);

        var result = await tagger.TagAsync(image);
        foreach (var tag in result.Tags)
        {
            _output.WriteLine($"{tag.Category} {tag.Name} {tag.Score:F3}");
        }

        Assert.NotEmpty(result.Tags);
    }
}
