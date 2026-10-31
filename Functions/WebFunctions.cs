using System.Net;
using System.Text;
using System.Text.Json;
using EntraOidcScimLab.Models;
using EntraOidcScimLab.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace EntraOidcScimLab.Functions;

public sealed class WebFunctions(LabConfig config, LabStorage storage, CookieSigner signer, OidcService oidc)
{
    [Function("Home")]
    public Task<HttpResponseData> Home([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "app")] HttpRequestData req)
        => StaticFile(req, "index.html", "text/html; charset=utf-8");

    [Function("Styles")]
    public Task<HttpResponseData> Styles([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "styles.css")] HttpRequestData req)
        => StaticFile(req, "styles.css", "text/css; charset=utf-8");

    [Function("AppJs")]
    public Task<HttpResponseData> AppJs([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "app.js")] HttpRequestData req)
        => StaticFile(req, "app.js", "application/javascript; charset=utf-8");

    [Function("Health")]
    public Task<HttpResponseData> Health([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
        => HttpHelpers.JsonAsync(req, new { status = "ok", app = "Entra OIDC + SCIM Lab (.NET 8 Azure Functions)", time = DateTimeOffset.UtcNow });

    [Function("Config")]
    public Task<HttpResponseData> Config([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/config")] HttpRequestData req)
    {
        var baseUrl = HttpHelpers.BaseUrl(req, config);
        return HttpHelpers.JsonAsync(req, new
        {
            oidcConfigured = config.OidcConfigured,
            scimConfigured = config.ScimConfigured,
            baseUrl,
            redirectUri = $"{baseUrl}/auth/redirect",
            scimBaseUrl = $"{baseUrl}/scim/v2"
        });
    }

    [Function("Me")]
    public async Task<HttpResponseData> Me([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/me")] HttpRequestData req)
    {
        var session = await GetSession(req);
        if (session is null) return await HttpHelpers.JsonAsync(req, new { authenticated = false });
        var user = JsonSerializer.Deserialize<Dictionary<string, object?>>(session.UserJson) ?? new();
        var claims = JsonSerializer.Deserialize<Dictionary<string, object?>>(session.ClaimsJson) ?? new();
        return await HttpHelpers.JsonAsync(req, new { authenticated = true, user, claims });
    }

    [Function("ScimUsersForDashboard")]
    public async Task<HttpResponseData> ScimUsersForDashboard([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/scim-users")] HttpRequestData req)
    {
        if (await GetSession(req) is null) return req.CreateResponse(HttpStatusCode.Unauthorized);
        var users = (await storage.GetUsersAsync()).Select(ScimFunctions.PublicUser).ToList();
        return await HttpHelpers.JsonAsync(req, users);
    }

    [Function("OidcLogin")]
    public async Task<HttpResponseData> OidcLogin([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/login")] HttpRequestData req)
    {
        if (!config.OidcConfigured) return await Text(req, HttpStatusCode.ServiceUnavailable, "OIDC is not configured. Set ENTRA_TENANT_ID, ENTRA_CLIENT_ID and ENTRA_CLIENT_SECRET.");
        if (string.IsNullOrWhiteSpace(config.SessionSecret)) return await Text(req, HttpStatusCode.ServiceUnavailable, "SESSION_SECRET is not configured.");

        var state = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { state, nonce, exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() })));
        var stateCookie = signer.Sign(raw);
        var baseUrl = HttpHelpers.BaseUrl(req, config);
        var redirectUri = $"{baseUrl}/auth/redirect";

        var response = req.CreateResponse(HttpStatusCode.Redirect);
        response.Headers.Add("Location", oidc.BuildAuthorizeUrl(redirectUri, state, nonce));
        response.Headers.Add("Set-Cookie", $"oidc_state={stateCookie}; Path=/; HttpOnly; SameSite=Lax; Max-Age=600{SecureSuffix(baseUrl)}");
        return response;
    }

    [Function("OidcRedirect")]
    public async Task<HttpResponseData> OidcRedirect([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/redirect")] HttpRequestData req)
    {
        var code = QueryValue(req.Url, "code");
        var returnedState = QueryValue(req.Url, "state");
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(returnedState))
            return await Text(req, HttpStatusCode.BadRequest, "Authorization code/state was not returned by Entra ID.");

        if (!signer.TryVerify(HttpHelpers.Cookie(req, "oidc_state"), out var raw))
            return await Text(req, HttpStatusCode.BadRequest, "OIDC state cookie validation failed.");

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        using var stateDoc = JsonDocument.Parse(json);
        var expectedState = stateDoc.RootElement.GetProperty("state").GetString();
        var nonce = stateDoc.RootElement.GetProperty("nonce").GetString();
        var exp = stateDoc.RootElement.GetProperty("exp").GetInt64();
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp || expectedState != returnedState || string.IsNullOrWhiteSpace(nonce))
            return await Text(req, HttpStatusCode.BadRequest, "OIDC state validation failed.");

        try
        {
            var baseUrl = HttpHelpers.BaseUrl(req, config);
            var result = await oidc.RedeemAndValidateAsync(code, $"{baseUrl}/auth/redirect", nonce);
            var principal = result.Principal;
            var user = new Dictionary<string, object?>
            {
                ["name"] = principal.FindFirst("name")?.Value ?? "Entra user",
                ["username"] = principal.FindFirst("preferred_username")?.Value ?? principal.FindFirst("email")?.Value ?? string.Empty,
                ["tenantId"] = principal.FindFirst("tid")?.Value ?? string.Empty,
                ["objectId"] = principal.FindFirst("oid")?.Value ?? string.Empty
            };
            var session = new SessionEntity
            {
                UserJson = JsonSerializer.Serialize(user),
                ClaimsJson = JsonSerializer.Serialize(result.Claims),
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
            };
            await storage.SaveSessionAsync(session);

            var response = req.CreateResponse(HttpStatusCode.Redirect);
            response.Headers.Add("Location", "/app");
            response.Headers.Add("Set-Cookie", $"lab_session={signer.Sign(session.RowKey)}; Path=/; HttpOnly; SameSite=Lax; Max-Age=3600{SecureSuffix(baseUrl)}");
            return response;
        }
        catch (Exception ex)
        {
            return await Text(req, HttpStatusCode.InternalServerError, $"Entra ID sign-in failed: {ex.Message}");
        }
    }

    [Function("OidcLogout")]
    public async Task<HttpResponseData> OidcLogout([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/logout")] HttpRequestData req)
    {
        if (signer.TryVerify(HttpHelpers.Cookie(req, "lab_session"), out var sessionId)) await storage.DeleteSessionAsync(sessionId);
        var baseUrl = HttpHelpers.BaseUrl(req, config);
        var appUrl = $"{baseUrl}/app";
        var logout = string.IsNullOrWhiteSpace(config.TenantId)
            ? appUrl
            : $"https://login.microsoftonline.com/{config.TenantId}/oauth2/v2.0/logout?post_logout_redirect_uri={Uri.EscapeDataString(appUrl)}";
        var response = req.CreateResponse(HttpStatusCode.Redirect);
        response.Headers.Add("Location", logout);
        response.Headers.Add("Set-Cookie", "lab_session=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0");
        return response;
    }

    private async Task<SessionEntity?> GetSession(HttpRequestData req)
        => signer.TryVerify(HttpHelpers.Cookie(req, "lab_session"), out var sessionId) ? await storage.GetSessionAsync(sessionId) : null;

    private static async Task<HttpResponseData> StaticFile(HttpRequestData req, string name, string contentType)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "public", name);
        if (!File.Exists(path)) return req.CreateResponse(HttpStatusCode.NotFound);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", contentType);
        await response.WriteStringAsync(await File.ReadAllTextAsync(path));
        return response;
    }

    private static string? QueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) == key) return pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty;
        }
        return null;
    }

    private static string SecureSuffix(string baseUrl) => baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "; Secure" : string.Empty;

    private static async Task<HttpResponseData> Text(HttpRequestData req, HttpStatusCode status, string text)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        await response.WriteStringAsync(text);
        return response;
    }
}
