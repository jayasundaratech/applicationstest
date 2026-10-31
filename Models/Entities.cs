using Azure;
using Azure.Data.Tables;

namespace EntraOidcScimLab.Models;

public sealed class ScimUserEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "user";
    public string RowKey { get; set; } = Guid.NewGuid().ToString();
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public string ExternalId { get; set; } = string.Empty;
    public string GivenName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string EmailsJson { get; set; } = "[]";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Version { get; set; } = string.Empty;
}

public sealed class SessionEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "session";
    public string RowKey { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string UserJson { get; set; } = "{}";
    public string ClaimsJson { get; set; } = "{}";
    public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.UtcNow.AddHours(1);
}
