using ClipNest.Ai.Tagging;
using Xunit.Abstractions;

namespace ClipNest.Ai.Tests;

/// <summary>
/// RAM++ 的真实权重冒烟测试。设置 CLIPNEST_RAM_DIR 与 CLIPNEST_TEST_IMAGE 后运行；
/// 未设置时跳过，不参与常规测试。
/// </summary>
public sealed class RamPlusSmokeTests
{
    private readonly ITestOutputHelper _output;

    public RamPlusSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RecognizesSampleImage()
    {
        var modelDir = Environment.GetEnvironmentVariable("CLIPNEST_RAM_DIR");
        var image = Environment.GetEnvironmentVariable("CLIPNEST_TEST_IMAGE");
        if (string.IsNullOrEmpty(modelDir) || string.IsNullOrEmpty(image))
        {
            _output.WriteLine("跳过：未设置 CLIPNEST_RAM_DIR / CLIPNEST_TEST_IMAGE");
            return;
        }

        var manager = new TagModelManager(modelDir, TagModelDefinition.RamPlus);
        using var tagger = new RamTagger(manager);

        var result = await tagger.TagAsync(image);
        foreach (var tag in result.Tags)
        {
            _output.WriteLine($"{tag.Category} {tag.Name} {tag.Score:F3}");
        }

        Assert.NotEmpty(result.Tags);
    }
}
