using System.Diagnostics;
using ClipNest.Ai;
using ClipNest.Ai.Tagging;
using ClipNest.Data;
using ClipNest.Data.Repositories;
using Xunit.Abstractions;

namespace ClipNest.Ai.Tests;

/// <summary>
/// 可选性能基准，需要 CLIPNEST_MODEL_DIR 与 CLIPNEST_DB 才运行。
/// 用于观察推理与落库的耗时；加速器换到 GPU 后，瓶颈通常已经不在推理。
/// </summary>
public sealed class TaggerBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public TaggerBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MeasurePipelineStages()
    {
        var modelDir = Environment.GetEnvironmentVariable("CLIPNEST_MODEL_DIR");
        var dbPath = Environment.GetEnvironmentVariable("CLIPNEST_DB");
        if (string.IsNullOrEmpty(modelDir) || string.IsNullOrEmpty(dbPath))
        {
            _output.WriteLine("跳过：未设置 CLIPNEST_MODEL_DIR / CLIPNEST_DB");
            return;
        }

        var manager = new TagModelManager(modelDir);
        using var tagger = new CamieTagger(manager);
        var database = new ClipNestDatabase(dbPath);
        var repository = new AiTagRepository(database);

        var jobs = await repository.GetPendingAsync(TagModelDefinition.Camie.Id, 6);
        if (jobs.Count == 0)
        {
            _output.WriteLine("跳过：图库里没有可用的样本");
            return;
        }

        // 第一次包含会话创建与着色器编译，单独统计。
        var sw = Stopwatch.StartNew();
        var first = await tagger.TagAsync(jobs[0].Path);
        _output.WriteLine($"首次（含加载）{sw.ElapsedMilliseconds} ms，{first.Tags.Count} 个标签");

        sw.Restart();
        await repository.ReplaceAsync(jobs[0].AssetId, TagModelDefinition.Camie.Id, first);
        _output.WriteLine($"落库 {sw.ElapsedMilliseconds} ms");

        var samples = new List<long>();
        foreach (var job in jobs.Skip(1))
        {
            sw.Restart();
            var result = await tagger.TagAsync(job.Path);
            samples.Add(sw.ElapsedMilliseconds);
            _ = result;
        }

        samples.Sort();
        _output.WriteLine($"热态 {samples.Count} 张：中位 {samples[samples.Count / 2]} ms，" +
                          $"最快 {samples[0]} ms，最慢 {samples[^1]} ms");
    }
}
