namespace EntraOidcScimLab.Services;

public sealed class LabConfig
{
    public string BaseUrl => (Environment.GetEnvironmentVariable("BASE_URL") ?? string.Empty).TrimEnd('/');
    public string TenantId => Environment.GetEnvironmentVariable("ENTRA_TENANT_ID") ?? string.Empty;
    public string ClientId => Environment.GetEnvironmentVariable("ENTRA_CLIENT_ID") ?? string.Empty;
    public string ClientSecret => Environment.GetEnvironmentVariable("ENTRA_CLIENT_SECRET") ?? string.Empty;
    public string SessionSecret => Environment.GetEnvironmentVariable("SESSION_SECRET") ?? string.Empty;
    public string ScimBearerToken => Environment.GetEnvironmentVariable("SCIM_BEARER_TOKEN") ?? string.Empty;

    public bool OidcConfigured => !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool ScimConfigured => !string.IsNullOrWhiteSpace(ScimBearerToken);
}
