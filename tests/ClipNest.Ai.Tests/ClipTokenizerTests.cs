using ClipNest.Ai;

namespace ClipNest.Ai.Tests;

public sealed class ClipTokenizerTests
{
    private const string Vocab =
        "[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\n海\n边\n的\n日\n落\n";

    [Fact]
    public void Encode_AddsSpecialTokensAndPadsToMaxLength()
    {
        var root = CreateTempDirectory();
        try
        {
            var vocabPath = Path.Combine(root, "vocab.txt");
            File.WriteAllText(vocabPath, Vocab);
            var tokenizer = new ClipTokenizer(vocabPath);

            var tokens = tokenizer.Encode("海边");

            Assert.Equal(ClipTokenizer.MaxLength, tokens.InputIds.Length);
            Assert.Equal(ClipTokenizer.MaxLength, tokens.AttentionMask.Length);
            // 词表中的特殊 token id 即词表索引；真实 BERT 词表中 [CLS]=101、[SEP]=102。
            Assert.Equal(new long[] { 2, 5, 6, 3 }, tokens.InputIds.Take(4).ToArray());
            Assert.Equal(new long[] { 1, 1, 1, 1 }, tokens.AttentionMask.Take(4).ToArray());
            Assert.All(tokens.AttentionMask.Skip(4), value => Assert.Equal(0, value));
            Assert.All(tokens.InputIds.Skip(4), value => Assert.Equal(0, value));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Encode_TruncatesLongTextKeepingSpecialTokens()
    {
        var root = CreateTempDirectory();
        try
        {
            var vocabPath = Path.Combine(root, "vocab.txt");
            File.WriteAllText(vocabPath, Vocab);
            var tokenizer = new ClipTokenizer(vocabPath);

            var tokens = tokenizer.Encode(new string('海', 100));

            Assert.Equal(ClipTokenizer.MaxLength, tokens.InputIds.Length);
            Assert.Equal(2, tokens.InputIds[0]);
            Assert.Equal(3, tokens.InputIds[^1]);
            Assert.All(tokens.AttentionMask, value => Assert.Equal(1, value));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-tokenizer-{Guid.NewGuid():N}");
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
