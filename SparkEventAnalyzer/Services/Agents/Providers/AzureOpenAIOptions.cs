namespace SparkEventAnalyzer.Services;

/// <summary>
/// Azure OpenAI configuration for the Spark analyst agent.
/// If <see cref="ApiKey"/> is set it is used; otherwise DefaultAzureCredential
/// (Managed Identity locally via `az login`) is used.
/// </summary>
public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>Azure OpenAI endpoint, e.g. https://my-aoai.openai.azure.com/ </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Deployment (model) name, e.g. "gpt-4o" or "gpt-5".</summary>
    public string Deployment { get; set; } = string.Empty;

    /// <summary>Optional API key. Leave empty to use Managed Identity / DefaultAzureCredential.</summary>
    public string? ApiKey { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Deployment);
}
