using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;

namespace EntraOidcScimLab.Services;

public static class HttpHelpers
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    public static async Task<HttpResponseData> JsonAsync(HttpRequestData req, object value, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK, string contentType = "application/json")
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", $"{contentType}; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(value, JsonOptions));
        return response;
    }

    public static string? Cookie(HttpRequestData req, string name)
        => req.Cookies.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))?.Value;

    public static string BaseUrl(HttpRequestData req, LabConfig config)
        => !string.IsNullOrWhiteSpace(config.BaseUrl) ? config.BaseUrl : $"{req.Url.Scheme}://{req.Url.Authority}";
}
