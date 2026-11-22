using ClipNest.Ai;
using ClipNest.Ai.Models;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace ClipNest.Ai.Tests;

/// <summary>
/// 语义搜索的人工评估，用真实图库验证检索质量与分数分布。
/// 需要 CLIPNEST_MODEL_DIR 与 CLIPNEST_DB 两个环境变量，缺省时跳过。
/// </summary>
public sealed class SemanticEvalTests
{
    private readonly ITestOutputHelper _output;

    public SemanticEvalTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ReportTopMatchesForChineseQueries()
    {
        var modelDir = Environment.GetEnvironmentVariable("CLIPNEST_MODEL_DIR");
        var dbPath = Environment.GetEnvironmentVariable("CLIPNEST_DB");
        if (string.IsNullOrWhiteSpace(modelDir) || string.IsNullOrWhiteSpace(dbPath)) return;

        var vectors = new Dictionary<long, float[]>();
        var names = new Dictionary<long, string>();

        using (var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            con.Open();
            using (var cmd = con.CreateCommand())
            {
                // 向量以 float16 小端存储，必须按 2 字节解码，直接 cast 成 float32 会得到大量 0。
                cmd.CommandText = "SELECT asset_id, dim, vector FROM asset_embeddings";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var bytes = (byte[])reader.GetValue(2);
                    var dim = (int)reader.GetInt64(1);
                    var vector = new float[dim];
                    for (var i = 0; i < dim; i++) vector[i] = (float)BitConverter.ToHalf(bytes, i * 2);
                    vectors[reader.GetInt64(0)] = vector;
                }
            }

            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT id, file_name FROM assets";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) names[reader.GetInt64(0)] = reader.GetString(1);
            }
        }

        _output.WriteLine($"向量 {vectors.Count} 条，资产 {names.Count} 条");

        var probeId = vectors.Keys.First();
        var probe = vectors[probeId];
        _output.WriteLine(
            $"样本向量 dim={probe.Length} norm={Norm(probe):F4} min={probe.Min():F4} max={probe.Max():F4}");

        using var encoder = new ClipEncoder(new ClipModelManager(modelDir));

        var probeText = await encoder.EncodeTextAsync("白发少女");
        _output.WriteLine(
            $"样本文本 dim={probeText.Length} norm={Norm(probeText):F4} " +
            $"min={probeText.Min():F4} max={probeText.Max():F4} " +
            $"前5={string.Join(",", probeText.Take(5).Select(v => v.ToString("F4")))}");
        _output.WriteLine($"样本文本 · 自身点积 {Dot(probeText, probeText):F4}；与样本图片 {Dot(probeText, probe):F4}");

        var queries = new[]
        {
            "白发少女", "戴帽子的女孩", "粉红色头发", "蓝天白云", "室内光线",
            "文字截图", "猫", "和服", "侧脸", "微笑的动漫女孩",
            "洗衣机", "汽车", "宇宙飞船", "教室", "下雨"
        };

        foreach (var query in queries)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = await encoder.EncodeTextAsync(query);
            var encodeMs = sw.ElapsedMilliseconds;

            var ranked = vectors
                .Select(pair => (Id: pair.Key, Score: Dot(text, pair.Value)))
                .OrderByDescending(item => item.Score)
                .ToList();

            _output.WriteLine($"\n[{query}] 文本编码 {encodeMs}ms  最高 {ranked[0].Score:F3}  " +
                              $"第5 {ranked[4].Score:F3}  中位 {ranked[ranked.Count / 2].Score:F3}  最低 {ranked[^1].Score:F3}");

            foreach (var (id, score) in ranked.Take(5))
                _output.WriteLine($"    {score:F3}  {names[id]}");
        }
    }

    private static double Norm(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector) sum += value * value;
        return Math.Sqrt(sum);
    }

    private static float Dot(float[] left, float[] right)
    {
        var sum = 0f;
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++) sum += left[i] * right[i];
        return sum;
    }
}
