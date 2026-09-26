using System.Diagnostics;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace RadioPlayer.Services;

/// <summary>
/// Local, offline embedding provider: all-MiniLM-L6-v2 (384-dim) via ONNX Runtime.
///
/// The model emits per-token hidden states. The correct sentence embedding is the
/// attention-mask-weighted MEAN over tokens, then L2-normalized — NOT the CLS token, and
/// pooling must not be skipped, or similarity is garbage (the classic MiniLM mistake).
/// </summary>
public sealed class MiniLmEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private const int MaxTokens = 256; // descriptions are short; bounds compute

    private readonly InferenceSession? _session;
    private readonly BertTokenizer? _tokenizer;
    private readonly string[] _inputNames;

    public string ModelId => "all-MiniLM-L6-v2";
    public int Dimension => 384;
    public bool IsAvailable { get; }

    public MiniLmEmbeddingProvider(string modelPath, string vocabPath)
    {
        try
        {
            if (!File.Exists(modelPath) || !File.Exists(vocabPath))
            {
                AppLog.Debug($"[Embed] model/vocab missing ({modelPath}); provider unavailable.");
                _inputNames = [];
                return;
            }

            _session = new InferenceSession(modelPath);
            _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = true });
            _inputNames = _session.InputMetadata.Keys.ToArray();
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[Embed] failed to initialise: {ex.Message}");
            _inputNames = [];
            IsAvailable = false;
        }
    }

    public float[]? Embed(string text)
    {
        if (!IsAvailable || _session is null || _tokenizer is null || string.IsNullOrWhiteSpace(text))
            return null;

        // 1) Tokenize (adds [CLS]/[SEP]); single, unpadded sequence → mask is all 1s.
        var ids = _tokenizer.EncodeToIds(text);
        if (ids.Count > MaxTokens)
            ids = ids.Take(MaxTokens).ToList();
        var seq = ids.Count;
        if (seq == 0)
            return null;

        var inputIds = new long[seq];
        var attentionMask = new long[seq];
        var tokenTypeIds = new long[seq];
        for (var i = 0; i < seq; i++)
        {
            inputIds[i] = ids[i];
            attentionMask[i] = 1;   // no padding in a single sequence
            tokenTypeIds[i] = 0;    // single segment
        }

        // 2) Run the model, feeding only the inputs it declares (names vary by export).
        var inputs = new List<NamedOnnxValue>(_inputNames.Length);
        foreach (var name in _inputNames)
        {
            var data = name.Contains("attention", StringComparison.OrdinalIgnoreCase) ? attentionMask
                     : name.Contains("token_type", StringComparison.OrdinalIgnoreCase) ? tokenTypeIds
                     : inputIds;
            inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data, [1, seq])));
        }

        using var results = _session.Run(inputs);
        var hidden = results.First().AsTensor<float>(); // [1, seq, dim]
        var dim = hidden.Dimensions[2];

        // 3) Mean-pool over tokens using the attention mask, then L2-normalize.
        var pooled = new float[dim];
        float maskSum = 0;
        for (var t = 0; t < seq; t++)
        {
            float m = attentionMask[t];
            maskSum += m;
            for (var h = 0; h < dim; h++)
                pooled[h] += hidden[0, t, h] * m;
        }
        if (maskSum <= 0)
            return null;

        double norm = 0;
        for (var h = 0; h < dim; h++)
        {
            pooled[h] /= maskSum;
            norm += pooled[h] * (double)pooled[h];
        }
        norm = Math.Sqrt(norm);
        if (norm > 0)
            for (var h = 0; h < dim; h++)
                pooled[h] = (float)(pooled[h] / norm);

        return pooled;
    }

    public void Dispose() => _session?.Dispose();
}
