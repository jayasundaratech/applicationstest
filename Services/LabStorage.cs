using Azure;
using Azure.Data.Tables;
using EntraOidcScimLab.Models;

namespace EntraOidcScimLab.Services;

public sealed class LabStorage
{
    private readonly TableClient _users;
    private readonly TableClient _sessions;

    public LabStorage()
    {
        var connection = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
            ?? throw new InvalidOperationException("AzureWebJobsStorage is required.");
        var service = new TableServiceClient(connection);
        _users = service.GetTableClient("ScimUsers");
        _sessions = service.GetTableClient("OidcSessions");
    }

    private async Task EnsureAsync()
    {
        await _users.CreateIfNotExistsAsync();
        await _sessions.CreateIfNotExistsAsync();
    }

    public async Task<List<ScimUserEntity>> GetUsersAsync()
    {
        await EnsureAsync();
        var results = new List<ScimUserEntity>();
        await foreach (var user in _users.QueryAsync<ScimUserEntity>(u => u.PartitionKey == "user")) results.Add(user);
        return results;
    }

    public async Task<ScimUserEntity?> GetUserAsync(string id)
    {
        await EnsureAsync();
        try { return (await _users.GetEntityAsync<ScimUserEntity>("user", id)).Value; }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task UpsertUserAsync(ScimUserEntity user)
    {
        await EnsureAsync();
        await _users.UpsertEntityAsync(user, TableUpdateMode.Replace);
    }

    public async Task DeleteUserAsync(string id)
    {
        await EnsureAsync();
        await _users.DeleteEntityAsync("user", id, ETag.All);
    }

    public async Task SaveSessionAsync(SessionEntity session)
    {
        await EnsureAsync();
        await _sessions.UpsertEntityAsync(session, TableUpdateMode.Replace);
    }

    public async Task<SessionEntity?> GetSessionAsync(string id)
    {
        await EnsureAsync();
        try
        {
            var session = (await _sessions.GetEntityAsync<SessionEntity>("session", id)).Value;
            if (session.ExpiresUtc <= DateTimeOffset.UtcNow)
            {
                await DeleteSessionAsync(id);
                return null;
            }
            return session;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task DeleteSessionAsync(string id)
    {
        await EnsureAsync();
        try { await _sessions.DeleteEntityAsync("session", id, ETag.All); }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
