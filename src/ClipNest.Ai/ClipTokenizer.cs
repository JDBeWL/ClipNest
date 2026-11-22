using Microsoft.ML.Tokenizers;

namespace ClipNest.Ai;

/// <summary>
/// 编码后的输入，含定长 token id 与注意力掩码。
/// </summary>
public sealed record ClipTokens(long[] InputIds, long[] AttentionMask);

/// <summary>
/// Chinese-CLIP 使用的 BERT WordPiece tokenizer。
/// </summary>
public sealed class ClipTokenizer
{
    public const int MaxLength = 77;

    private readonly BertTokenizer _tokenizer;
    private readonly int _maxLength;
    private readonly long _padId;

    public ClipTokenizer(string vocabPath, int maxLength = MaxLength)
    {
        _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions
        {
            LowerCaseBeforeTokenization = true,
            IndividuallyTokenizeCjk = true
        });
        _maxLength = maxLength;
        _padId = _tokenizer.PaddingTokenId;
    }

    /// <summary>
    /// 编码文本并补齐到固定长度；EncodeToIds 已包含 [CLS]/[SEP]，不再重复添加。
    /// </summary>
    public ClipTokens Encode(string text)
    {
        var encoded = _tokenizer.EncodeToIds(text);
        var inputIds = new long[_maxLength];
        var attentionMask = new long[_maxLength];
        Array.Fill(inputIds, _padId);

        if (encoded.Count > _maxLength)
        {
            // 超长时保留 [CLS]、前 _maxLength-2 个内容 token 与 [SEP]。
            inputIds[0] = encoded[0];
            for (var i = 1; i < _maxLength - 1; i++)
            {
                inputIds[i] = encoded[i];
            }

            inputIds[_maxLength - 1] = encoded[encoded.Count - 1];
            Array.Fill(attentionMask, 1L);
            return new ClipTokens(inputIds, attentionMask);
        }

        for (var i = 0; i < encoded.Count; i++)
        {
            inputIds[i] = encoded[i];
            attentionMask[i] = 1L;
        }

        return new ClipTokens(inputIds, attentionMask);
    }
}
