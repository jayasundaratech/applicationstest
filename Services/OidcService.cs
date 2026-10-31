using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EntraOidcScimLab.Services;

public sealed class OidcService(LabConfig config)
{
    private static readonly HttpClient Http = new();

    public string BuildAuthorizeUrl(string redirectUri, string state, string nonce)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = "openid profile email",
            ["state"] = state,
            ["nonce"] = nonce,
            ["prompt"] = "select_account"
        };
        var qs = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"https://login.microsoftonline.com/{config.TenantId}/oauth2/v2.0/authorize?{qs}";
    }

    public async Task<(ClaimsPrincipal Principal, Dictionary<string, object?> Claims)> RedeemAndValidateAsync(string code, string redirectUri, string expectedNonce)
    {
        var tokenEndpoint = $"https://login.microsoftonline.com/{config.TenantId}/oauth2/v2.0/token";
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = config.ClientId,
                ["client_secret"] = config.ClientSecret,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["scope"] = "openid profile email"
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await Http.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Entra token exchange failed: {payload}");

        using var tokenJson = JsonDocument.Parse(payload);
        var idToken = tokenJson.RootElement.GetProperty("id_token").GetString()
            ?? throw new InvalidOperationException("No id_token returned by Entra ID.");

        var metadata = $"https://login.microsoftonline.com/{config.TenantId}/v2.0/.well-known/openid-configuration";
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(metadata, new OpenIdConnectConfigurationRetriever());
        var oidc = await manager.GetConfigurationAsync(CancellationToken.None);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = oidc.Issuer,
            ValidateAudience = true,
            ValidAudience = config.ClientId,
            ValidateLifetime = true,
            RequireSignedTokens = true,
            IssuerSigningKeys = oidc.SigningKeys,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(idToken, parameters, out _);
        var nonce = principal.FindFirst("nonce")?.Value;
        if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            throw new SecurityTokenValidationException("OIDC nonce validation failed.");

        var claims = principal.Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? (object?)g.First().Value : g.Select(x => x.Value).ToArray());

        return (principal, claims);
    }
}
