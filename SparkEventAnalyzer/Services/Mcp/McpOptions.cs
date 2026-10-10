namespace SparkEventAnalyzer.Services;

/// <summary>
/// Configuration for the remote Azure DevOps MCP server (the "Mcp:AzureDevOps" section).
///
/// The agent connects to the hosted ADO MCP endpoint (Streamable HTTP) and authenticates
/// as the signed-in user via a delegated (device-code) token scoped to the MCP resource.
/// No managed identity, PAT, or app registration is required for a prototype: a well-known
/// public client id (Azure CLI) is reused to acquire the token, exactly as
/// <c>az account get-access-token --scope https://mcp.dev.azure.com/.default</c> does.
/// </summary>
public sealed class AzureDevOpsMcpOptions
{
    public const string SectionName = "Mcp:AzureDevOps";

    /// <summary>Feature flag. When false the MCP client is not registered and no tools are added.</summary>
    public bool Enabled { get; set; }

    /// <summary>Remote MCP endpoint, e.g. https://mcp.dev.azure.com/{organization}</summary>
    public string Url { get; set; } = "https://mcp.dev.azure.com/msazure";

    /// <summary>Token scope the endpoint requires (audience = the ADO MCP resource).</summary>
    public string Scope { get; set; } = "https://mcp.dev.azure.com/.default";

    /// <summary>
    /// Public client id used to acquire the delegated token. Defaults to the Azure CLI
    /// client id, which is a pre-consented public client that can request the MCP scope.
    /// Replace with your own registered client id for a durable deployment.
    /// </summary>
    public string ClientId { get; set; } = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    /// <summary>
    /// Registered SPA (Single-Page Application) client id used by the browser to acquire the
    /// MCP token via Authorization Code + PKCE (MSAL.js). Required for AuthMode=Browser, which
    /// is the only flow that works when the backend runs headless (e.g. Azure Container Apps).
    /// </summary>
    public string? SpaClientId { get; set; }

    /// <summary>Entra tenant id used as the authority.</summary>
    public string TenantId { get; set; } = "<tenant-id>";

    /// <summary>
    /// How the user signs in. Interactive pops the system browser (works when device-code
    /// flow is blocked by Conditional Access); DeviceCode shows a code + verification URL.
    /// Interactive requires the host to be able to open a browser (local dev / desktop).
    /// </summary>
    public McpAuthMode AuthMode { get; set; } = McpAuthMode.Interactive;

    /// <summary>
    /// Optional X-MCP-Toolsets value to limit the exposed tool surface
    /// (e.g. "repos,wit"). Empty => all tools.
    /// </summary>
    public string? Toolsets { get; set; }

    /// <summary>When true, sends X-MCP-Readonly:true so write tools are excluded.</summary>
    public bool ReadOnly { get; set; } = true;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(Url)
        && !string.IsNullOrWhiteSpace(Scope)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(TenantId);
}

/// <summary>User sign-in method for the delegated ADO MCP token.</summary>
public enum McpAuthMode
{
    /// <summary>Pop the system browser (Authorization Code + PKCE). Not blocked by device-code CA policies.</summary>
    Interactive,

    /// <summary>Show a device code + verification URL. Blocked by some Conditional Access policies.</summary>
    DeviceCode,

    /// <summary>
    /// Sign-in happens in the user's browser (MSAL.js, Authorization Code + PKCE) using the
    /// registered SPA client id; the browser posts the token to the backend. The only flow
    /// that works when the backend is headless (container).
    /// </summary>
    Browser,
}

/// <summary>Snapshot of a device-code sign-in prompt returned to the caller of /api/ado/login.</summary>
public sealed record DeviceLoginInfo(
    string Status,
    string? Message,
    string? UserCode,
    string? VerificationUri,
    DateTimeOffset? ExpiresOn,
    string? Account);
