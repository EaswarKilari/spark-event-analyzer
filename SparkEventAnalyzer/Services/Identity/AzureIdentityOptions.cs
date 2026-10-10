namespace SparkEventAnalyzer.Services;

/// <summary>
/// Azure identity configuration (the "AzureIdentity" section). Controls the single shared
/// <see cref="Azure.Core.TokenCredential"/> used for Foundry, Azure OpenAI, and the Spark
/// History Server.
/// </summary>
public sealed class AzureIdentityOptions
{
    public const string SectionName = "AzureIdentity";

    /// <summary>
    /// Client id (GUID) of a <b>user-assigned</b> Managed Identity to authenticate as.
    /// Leave empty to use <c>DefaultAzureCredential</c> — which resolves a system-assigned
    /// Managed Identity in Azure and your <c>az login</c> session locally.
    /// </summary>
    public string? ManagedIdentityClientId { get; set; }
}
