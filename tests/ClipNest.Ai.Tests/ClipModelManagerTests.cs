using ClipNest.Ai.Models;

namespace ClipNest.Ai.Tests;

public sealed class ClipModelManagerTests
{
    [Fact]
    public void IsReady_FalseWhenFilesMissing()
    {
        var root = CreateTempDirectory();
        try
        {
            var manager = new ClipModelManager(root);

            Assert.False(manager.IsReady);
            Assert.Equal(root, manager.Root);
            Assert.Equal(Path.Combine(root, "model_quantized.onnx"), manager.ModelPath);
            Assert.Equal(Path.Combine(root, "vocab.txt"), manager.VocabPath);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void IsReady_TrueWhenBothFilesExist()
    {
        var root = CreateTempDirectory();
        try
        {
            var definition = new ClipModelDefinition
            {
                Id = "test",
                DisplayName = "test",
                Dimension = 2,
                License = "test",
                BaseUrls = new[] { "https://test/" },
                Files = new[]
                {
                    new ClipModelFile("model.onnx", "model.onnx", 5),
                    new ClipModelFile("vocab.txt", "vocab.txt", 5)
                }
            };
            var manager = new ClipModelManager(root, definition);
            File.WriteAllText(manager.ModelPath, "model");
            File.WriteAllText(manager.VocabPath, "vocab");

            Assert.True(manager.IsReady);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
