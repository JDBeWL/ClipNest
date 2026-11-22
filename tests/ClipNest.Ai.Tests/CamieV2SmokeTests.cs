using ClipNest.Ai.Tagging;
using Xunit.Abstractions;

namespace ClipNest.Ai.Tests;

/// <summary>
/// CAMIE v2 的真实权重冒烟测试。设置 CLIPNEST_CAMIE_V2_DIR 与 CLIPNEST_TEST_IMAGE 后运行；
/// 未设置时跳过，不参与常规测试。
/// </summary>
public sealed class CamieV2SmokeTests
{
    private readonly ITestOutputHelper _output;

    public CamieV2SmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RecognizesSampleImage()
    {
        var modelDir = Environment.GetEnvironmentVariable("CLIPNEST_CAMIE_V2_DIR");
        var image = Environment.GetEnvironmentVariable("CLIPNEST_TEST_IMAGE");
        if (string.IsNullOrEmpty(modelDir) || string.IsNullOrEmpty(image))
        {
            _output.WriteLine("跳过：未设置 CLIPNEST_CAMIE_V2_DIR / CLIPNEST_TEST_IMAGE");
            return;
        }

        var manager = new TagModelManager(modelDir, TagModelDefinition.CamieV2);
        using var tagger = new CamieTagger(manager);

        var result = await tagger.TagAsync(image);
        foreach (var tag in result.Tags)
        {
            _output.WriteLine($"{tag.Category} {tag.Name} {tag.Score:F3}");
        }

        Assert.NotEmpty(result.Tags);
    }
}
