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

    [Function("ScimOnlyPage")]
    public async Task<HttpResponseData> ScimOnlyPage([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scim-only")] HttpRequestData req)
    {
        var session = await GetSession(req);
        if (session is null)
        {
            var redirect = req.CreateResponse(HttpStatusCode.Redirect);
            redirect.Headers.Add("Location", "/auth/login");
            return redirect;
        }

        var username = GetSessionUsername(session);
        var scimUser = await FindScimUser(username);

        if (scimUser is null)
            return await PermissionPage(req, username, "Your account has not been provisioned to this application through SCIM.");

        if (!scimUser.Active)
            return await PermissionPage(req, username, "Your SCIM account exists, but it is currently disabled.");

        return await ProtectedPage(req, username, scimUser.DisplayName);
    }

    [Function("ScimOnlyApi")]
    public async Task<HttpResponseData> ScimOnlyApi([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/scim-only")] HttpRequestData req)
    {
        var session = await GetSession(req);
        if (session is null)
            return await HttpHelpers.JsonAsync(req, new { allowed = false, message = "Sign in is required." }, HttpStatusCode.Unauthorized);

        var username = GetSessionUsername(session);
        var scimUser = await FindScimUser(username);

        if (scimUser is null || !scimUser.Active)
            return await HttpHelpers.JsonAsync(req, new
            {
                allowed = false,
                user = username,
                message = "No permission to access this API. An active SCIM-provisioned account is required."
            }, HttpStatusCode.Forbidden);

        return await HttpHelpers.JsonAsync(req, new
        {
            allowed = true,
            user = username,
            message = "Access granted. This response is only available to active SCIM users.",
            secureData = new
            {
                environment = "STAGING",
                feature = "SCIM protected API",
                time = DateTimeOffset.UtcNow
            }
        });
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

    private static string GetSessionUsername(SessionEntity session)
    {
        var user = JsonSerializer.Deserialize<Dictionary<string, object?>>(session.UserJson) ?? new();
        return user.TryGetValue("username", out var username) ? username?.ToString() ?? string.Empty : string.Empty;
    }

    private async Task<ScimUserEntity?> FindScimUser(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var users = await storage.GetUsersAsync();
        return users.FirstOrDefault(user => string.Equals(user.UserName, username, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<HttpResponseData> PermissionPage(HttpRequestData req, string username, string reason)
    {
        var safeUser = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(username) ? "Unknown user" : username);
        var safeReason = WebUtility.HtmlEncode(reason);
        var response = req.CreateResponse(HttpStatusCode.Forbidden);
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");
        await response.WriteStringAsync($$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>No permission</title>
  <link rel="stylesheet" href="/styles.css" />
</head>
<body>
  <main class="shell" style="max-width:860px;padding-top:90px">
    <section class="card" style="padding:42px;text-align:center">
      <p class="kicker">SCIM protected page</p>
      <h1 style="font-size:2.5rem;margin-bottom:20px">No permission to access this page</h1>
      <p class="subtitle" style="margin-left:auto;margin-right:auto">You successfully authenticated with Microsoft Entra ID, but this page requires an active SCIM-provisioned application account.</p>
      <div style="margin:30px auto;max-width:650px;text-align:left;background:#0a1727;border:1px solid #3f1d2d;border-radius:14px;padding:20px">
        <p><strong>Signed-in user:</strong> {{safeUser}}</p>
        <p><strong>Reason:</strong> {{safeReason}}</p>
      </div>
      <div class="hero-actions" style="justify-content:center">
        <a class="button primary" href="/app">Back to identity dashboard</a>
        <a class="button ghost" href="/auth/logout">Sign out</a>
      </div>
    </section>
  </main>
</body>
</html>
""");
        return response;
    }

    private static async Task<HttpResponseData> ProtectedPage(HttpRequestData req, string username, string displayName)
    {
        var safeUser = WebUtility.HtmlEncode(username);
        var safeName = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(displayName) ? username : displayName);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");
        await response.WriteStringAsync($$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>SCIM Protected Area</title>
  <link rel="stylesheet" href="/styles.css" />
</head>
<body>
  <main class="shell" style="max-width:900px;padding-top:70px">
    <section class="card" style="padding:42px">
      <p class="kicker">SCIM protected page</p>
      <h1 style="font-size:2.8rem">Welcome, {{safeName}}</h1>
      <p class="subtitle">This page was served only after the backend verified both your OIDC session and your active SCIM account.</p>
      <div class="decision-grid" style="margin-top:30px">
        <div class="decision-check good"><span class="check-icon">✓</span><div><strong>OIDC authenticated</strong><small>{{safeUser}}</small></div></div>
        <div class="decision-operator">+</div>
        <div class="decision-check good"><span class="check-icon">✓</span><div><strong>SCIM active</strong><small>Provisioned account found</small></div></div>
        <div class="decision-operator">=</div>
        <div class="decision-result allow"><strong>ACCESS GRANTED</strong><br />Backend authorization succeeded.</div>
      </div>
      <div style="margin-top:30px;padding:22px;background:#0a1727;border:1px solid #20364e;border-radius:14px">
        <h2>Protected sample content</h2>
        <p class="muted">Only active SCIM users can read this section. You can use the same server-side check around finance pages, admin tools, APIs, reports, or other application features.</p>
        <p><a class="button ghost" href="/api/scim-only">Open protected API response</a></p>
      </div>
      <div class="hero-actions" style="margin-top:28px">
        <a class="button primary" href="/app">Back to identity dashboard</a>
        <a class="button ghost" href="/auth/logout">Sign out</a>
      </div>
    </section>
  </main>
</body>
</html>
""");
        return response;
    }

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
