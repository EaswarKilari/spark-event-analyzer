using System.ClientModel;
using Azure.AI.OpenAI;
using OpenAI.Embeddings;

namespace CodeIndexer;

/// <summary>
/// Generates embeddings with Azure OpenAI (text-embedding-3-large) using dimension reduction.
/// Batches inputs and retries transient 429/503 responses with exponential backoff.
/// </summary>
public sealed class EmbeddingGenerator
{
    private readonly EmbeddingClient _client;
    private readonly int _dimensions;
    private int _retries;

    public int Retries => Volatile.Read(ref _retries);

    public EmbeddingGenerator(AzureOpenAIClient client, string deployment, int dimensions)
    {
        _client = client.GetEmbeddingClient(deployment);
        _dimensions = dimensions;
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var options = new EmbeddingGenerationOptions { Dimensions = _dimensions };

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                OpenAIEmbeddingCollection result = await _client.GenerateEmbeddingsAsync(inputs, options, ct);
                return result.Select(e => e.ToFloats().ToArray()).ToList();
            }
            catch (ClientResultException ex) when ((ex.Status == 429 || ex.Status == 503) && attempt < 8)
            {
                Interlocked.Increment(ref _retries);
                var delayMs = (int)Math.Min(30_000, 1000 * Math.Pow(2, attempt));
                await Task.Delay(delayMs, ct);
            }
        }
    }
}
