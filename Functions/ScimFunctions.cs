using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using EntraOidcScimLab.Models;
using EntraOidcScimLab.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace EntraOidcScimLab.Functions;

public sealed class ScimFunctions(LabConfig config, LabStorage storage)
{
    [Function("ScimServiceProviderConfig")]
    public async Task<HttpResponseData> ServiceProviderConfig([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scim/v2/ServiceProviderConfig")] HttpRequestData req)
    {
        if (!Authorized(req)) return await ScimError(req, HttpStatusCode.Unauthorized, "Invalid or missing bearer token.");
        return await ScimJson(req, new Dictionary<string, object?>
        {
            ["schemas"] = new[] { "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig" },
            ["patch"] = new { supported = true },
            ["bulk"] = new { supported = false, maxOperations = 0, maxPayloadSize = 0 },
            ["filter"] = new { supported = true, maxResults = 200 },
            ["changePassword"] = new { supported = false },
            ["sort"] = new { supported = false },
            ["etag"] = new { supported = false },
            ["authenticationSchemes"] = new[] { new { type = "oauthbearertoken", name = "Bearer Token", description = "Static bearer token for this lab." } }
        });
    }

    [Function("ScimResourceTypes")]
    public async Task<HttpResponseData> ResourceTypes([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scim/v2/ResourceTypes")] HttpRequestData req)
    {
        if (!Authorized(req)) return await ScimError(req, HttpStatusCode.Unauthorized, "Invalid or missing bearer token.");
        return await ScimJson(req, new Dictionary<string, object?>
        {
            ["schemas"] = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
            ["totalResults"] = 1,
            ["Resources"] = new[] { new { id = "User", name = "User", endpoint = "/Users", schema = "urn:ietf:params:scim:schemas:core:2.0:User" } }
        });
    }

    [Function("ScimSchemas")]
    public async Task<HttpResponseData> Schemas([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scim/v2/Schemas")] HttpRequestData req)
    {
        if (!Authorized(req)) return await ScimError(req, HttpStatusCode.Unauthorized, "Invalid or missing bearer token.");
        return await ScimJson(req, new Dictionary<string, object?>
        {
            ["schemas"] = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
            ["totalResults"] = 1,
            ["Resources"] = new[] { new { id = "urn:ietf:params:scim:schemas:core:2.0:User", name = "User", description = "SCIM core user schema for the Entra lab." } }
        });
    }

    [Function("ScimUsers")]
    public async Task<HttpResponseData> Users([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "scim/v2/Users")] HttpRequestData req)
    {
        if (!Authorized(req)) return await ScimError(req, HttpStatusCode.Unauthorized, "Invalid or missing bearer token.");

        if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            var users = await storage.GetUsersAsync();
            var filter = QueryValue(req.Url, "filter");
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var match = System.Text.RegularExpressions.Regex.Match(filter, "^userName\\s+eq\\s+\\\"([^\\\"]+)\\\"$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success) return await ScimError(req, HttpStatusCode.BadRequest, "This lab supports filters in the form: userName eq \"user@domain.com\"");
                users = users.Where(u => string.Equals(u.UserName, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            return await ScimJson(req, new Dictionary<string, object?>
            {
                ["schemas"] = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
                ["totalResults"] = users.Count,
                ["startIndex"] = 1,
                ["itemsPerPage"] = users.Count,
                ["Resources"] = users.Select(PublicUser).ToArray()
            });
        }

        using var body = await JsonDocument.ParseAsync(req.Body);
        var root = body.RootElement;
        var userName = GetString(root, "userName");
        if (string.IsNullOrWhiteSpace(userName)) return await ScimError(req, HttpStatusCode.BadRequest, "userName is required.");

        var existing = (await storage.GetUsersAsync()).Any(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase));
        if (existing) return await ScimError(req, HttpStatusCode.Conflict, "A user with this userName already exists.");

        var now = DateTimeOffset.UtcNow;
        var user = FromBody(root, now);
        await storage.UpsertUserAsync(user);
        var response = await ScimJson(req, PublicUser(user), HttpStatusCode.Created);
        response.Headers.Add("Location", $"{HttpHelpers.BaseUrl(req, config)}/scim/v2/Users/{user.RowKey}");
        return response;
    }

    [Function("ScimUserById")]
    public async Task<HttpResponseData> UserById([HttpTrigger(AuthorizationLevel.Anonymous, "get", "put", "patch", "delete", Route = "scim/v2/Users/{id}")] HttpRequestData req, string id)
    {
        if (!Authorized(req)) return await ScimError(req, HttpStatusCode.Unauthorized, "Invalid or missing bearer token.");
        var user = await storage.GetUserAsync(id);
        if (user is null) return await ScimError(req, HttpStatusCode.NotFound, "User not found.");

        if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)) return await ScimJson(req, PublicUser(user));
        if (req.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            await storage.DeleteUserAsync(id);
            return req.CreateResponse(HttpStatusCode.NoContent);
        }

        using var body = await JsonDocument.ParseAsync(req.Body);
        if (req.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
        {
            var userName = GetString(body.RootElement, "userName");
            if (string.IsNullOrWhiteSpace(userName)) return await ScimError(req, HttpStatusCode.BadRequest, "userName is required.");
            ApplyPut(user, body.RootElement);
        }
        else ApplyPatch(user, body.RootElement);

        user.LastModifiedUtc = DateTimeOffset.UtcNow;
        user.Version = $"W/\"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\"";
        await storage.UpsertUserAsync(user);
        return await ScimJson(req, PublicUser(user));
    }

    public static Dictionary<string, object?> PublicUser(ScimUserEntity user) => new()
    {
        ["schemas"] = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
        ["id"] = user.RowKey,
        ["externalId"] = string.IsNullOrWhiteSpace(user.ExternalId) ? null : user.ExternalId,
        ["userName"] = user.UserName,
        ["displayName"] = user.DisplayName,
        ["active"] = user.Active,
        ["name"] = new { givenName = user.GivenName, familyName = user.FamilyName },
        ["emails"] = JsonSerializer.Deserialize<object[]>(user.EmailsJson) ?? Array.Empty<object>(),
        ["meta"] = new { resourceType = "User", created = user.CreatedUtc, lastModified = user.LastModifiedUtc, version = user.Version }
    };

    private bool Authorized(HttpRequestData req)
    {
        if (string.IsNullOrWhiteSpace(config.ScimBearerToken)) return false;
        var header = req.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var supplied = System.Text.Encoding.UTF8.GetBytes(header[7..]);
        var expected = System.Text.Encoding.UTF8.GetBytes(config.ScimBearerToken);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static ScimUserEntity FromBody(JsonElement root, DateTimeOffset now)
    {
        var name = root.TryGetProperty("name", out var nameEl) ? nameEl : default;
        var userName = GetString(root, "userName")!;
        return new ScimUserEntity
        {
            RowKey = Guid.NewGuid().ToString(),
            UserName = userName,
            DisplayName = GetString(root, "displayName") ?? userName,
            Active = !root.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.False,
            ExternalId = GetString(root, "externalId") ?? string.Empty,
            GivenName = name.ValueKind == JsonValueKind.Object ? GetString(name, "givenName") ?? string.Empty : string.Empty,
            FamilyName = name.ValueKind == JsonValueKind.Object ? GetString(name, "familyName") ?? string.Empty : string.Empty,
            EmailsJson = root.TryGetProperty("emails", out var emails) ? emails.GetRawText() : "[]",
            CreatedUtc = now,
            LastModifiedUtc = now,
            Version = $"W/\"{now.ToUnixTimeMilliseconds()}\""
        };
    }

    private static void ApplyPut(ScimUserEntity user, JsonElement root)
    {
        user.UserName = GetString(root, "userName") ?? user.UserName;
        user.DisplayName = GetString(root, "displayName") ?? user.UserName;
        user.ExternalId = GetString(root, "externalId") ?? string.Empty;
        user.Active = !root.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.False;
        if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object)
        {
            user.GivenName = GetString(name, "givenName") ?? string.Empty;
            user.FamilyName = GetString(name, "familyName") ?? string.Empty;
        }
        user.EmailsJson = root.TryGetProperty("emails", out var emails) ? emails.GetRawText() : "[]";
    }

    private static void ApplyPatch(ScimUserEntity user, JsonElement root)
    {
        if (!root.TryGetProperty("Operations", out var operations) || operations.ValueKind != JsonValueKind.Array) return;
        foreach (var operation in operations.EnumerateArray())
        {
            var op = (GetString(operation, "op") ?? string.Empty).ToLowerInvariant();
            var path = GetString(operation, "path") ?? string.Empty;
            if (!operation.TryGetProperty("value", out var value)) continue;

            if (string.IsNullOrWhiteSpace(path) && value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("active", out var active)) user.Active = active.ValueKind == JsonValueKind.True;
                if (value.TryGetProperty("userName", out var un)) user.UserName = un.GetString() ?? user.UserName;
                if (value.TryGetProperty("displayName", out var dn)) user.DisplayName = dn.GetString() ?? user.DisplayName;
                continue;
            }

            var simple = path.Split('[')[0];
            if (op == "remove")
            {
                if (simple == "emails") user.EmailsJson = "[]";
                else if (simple == "displayName") user.DisplayName = string.Empty;
                else if (simple == "externalId") user.ExternalId = string.Empty;
                continue;
            }

            if (simple == "active") user.Active = value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && value.GetString() == "true");
            else if (simple == "userName") user.UserName = value.GetString() ?? user.UserName;
            else if (simple == "displayName") user.DisplayName = value.GetString() ?? user.DisplayName;
            else if (simple == "externalId") user.ExternalId = value.GetString() ?? string.Empty;
            else if (simple == "emails") user.EmailsJson = value.GetRawText();
            else if (simple == "name" && value.ValueKind == JsonValueKind.Object)
            {
                user.GivenName = GetString(value, "givenName") ?? user.GivenName;
                user.FamilyName = GetString(value, "familyName") ?? user.FamilyName;
            }
        }
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

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Task<HttpResponseData> ScimJson(HttpRequestData req, object value, HttpStatusCode status = HttpStatusCode.OK)
        => HttpHelpers.JsonAsync(req, value, status, "application/scim+json");

    private static Task<HttpResponseData> ScimError(HttpRequestData req, HttpStatusCode status, string detail)
        => ScimJson(req, new Dictionary<string, object?>
        {
            ["schemas"] = new[] { "urn:ietf:params:scim:api:messages:2.0:Error" },
            ["status"] = ((int)status).ToString(),
            ["detail"] = detail
        }, status);
}
