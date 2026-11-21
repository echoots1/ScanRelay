using System.Text.Json.Nodes;
using ScanRelay.Core.Install;

namespace ScanRelay.Core.Cloud;

public sealed record DirectoryGroup(Guid Id, string DisplayName);

public sealed record DirectoryUser(Guid Id, string DisplayName, string UserPrincipalName, bool Enabled, bool IsGuest);

public sealed record UploadedItem(string DriveId, string ItemId, string Name);

/// <summary>Everything the running service asks of Microsoft 365. Tests replace it or point it at a mock.</summary>
public interface ICloud
{
    Task<string> GetOrganizationNameAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetGrantedPermissionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string text, CancellationToken ct = default);
    Task<DirectoryGroup?> GetGroupAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryUser>> GetGroupMembersAsync(Guid groupId, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string text, CancellationToken ct = default);
    Task<DirectoryUser?> GetUserAsync(Guid id, CancellationToken ct = default);
    Task<bool> IsMemberAsync(Guid userId, Guid groupId, CancellationToken ct = default);
    Task<bool> HasOneDriveAsync(Guid userId, CancellationToken ct = default);
    Task<UploadedItem> UploadAsync(Guid userId, OneDriveTarget target, string filePath, CancellationToken ct = default);
    Task<bool> ItemExistsAsync(string driveId, string itemId, CancellationToken ct = default);
    Task SendMailAsync(string sender, IReadOnlyList<string> recipients, string subject, string textBody, CancellationToken ct = default);
}

/// <summary>Where an upload goes and what to call it.</summary>
/// <param name="UnderDocuments">Start from the built-in Documents folder, not the drive's root.</param>
/// <param name="Folders">Folders to walk into, created when missing.</param>
/// <param name="BaseName">The file name without extension. A clash gets a numbered suffix.</param>
public sealed record OneDriveTarget(bool UnderDocuments, IReadOnlyList<string> Folders, string BaseName, string Extension);

public sealed class GraphCloud(HttpClient http, InstallConfig install, AppTokenProvider tokens, TimeProvider time) : ICloud
{
    /// <summary>Graph takes files up to this size in one call. Larger files use an upload session.</summary>
    public long SingleCallLimit { get; init; } = 250L * 1024 * 1024;

    /// <summary>Upload session pieces must be a multiple of 320 KiB.</summary>
    public int ChunkSize { get; init; } = 320 * 1024 * 32;

    private const string UserFields = "id,displayName,userPrincipalName,accountEnabled,userType";
    private readonly GraphHttp _graph = new(http, install.Cloud.Graph, tokens.GetTokenAsync, time);

    public async Task<string> GetOrganizationNameAsync(CancellationToken ct = default)
    {
        var result = await _graph.GetAsync("organization?$select=displayName", ct);
        return result["value"]?[0]?["displayName"]?.GetValue<string>() ?? "";
    }

    public Task<IReadOnlyList<string>> GetGrantedPermissionsAsync(CancellationToken ct = default) =>
        tokens.GetGrantedRolesAsync(ct);

    public async Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string text, CancellationToken ct = default)
    {
        var filter = string.IsNullOrWhiteSpace(text) ? "" : $"&$filter=startswith(displayName,'{Escape(text.Trim())}')";
        var result = await _graph.GetAsync($"groups?$select=id,displayName&$top=25{filter}", ct);
        return [.. (result["value"] as JsonArray ?? []).Select(g => ToGroup(g!.AsObject())).OrderBy(g => g.DisplayName)];
    }

    public async Task<DirectoryGroup?> GetGroupAsync(Guid id, CancellationToken ct = default) =>
        await _graph.GetOrNullAsync($"groups/{id}?$select=id,displayName", ct) is { } g ? ToGroup(g) : null;

    public async Task<IReadOnlyList<DirectoryUser>> GetGroupMembersAsync(Guid groupId, CancellationToken ct = default)
    {
        // Members of nested groups count too. The cast keeps only users.
        var items = await _graph.GetAllAsync($"groups/{groupId}/transitiveMembers/microsoft.graph.user?$select={UserFields}&$top=999", ct);
        return [.. items.Select(ToUser)];
    }

    public async Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var t = Escape(text.Trim());
        var result = await _graph.GetAsync(
            $"users?$select={UserFields}&$top=25&$filter=startswith(displayName,'{t}') or startswith(userPrincipalName,'{t}') or startswith(surname,'{t}')", ct);
        return [.. (result["value"] as JsonArray ?? []).Select(u => ToUser(u!.AsObject())).OrderBy(u => u.DisplayName)];
    }

    public async Task<DirectoryUser?> GetUserAsync(Guid id, CancellationToken ct = default) =>
        await _graph.GetOrNullAsync($"users/{id}?$select={UserFields}", ct) is { } u ? ToUser(u) : null;

    public async Task<bool> IsMemberAsync(Guid userId, Guid groupId, CancellationToken ct = default)
    {
        var result = await _graph.PostAsync(
            $"users/{userId}/checkMemberGroups",
            new JsonObject { ["groupIds"] = new JsonArray(groupId.ToString()) },
            ct);
        return (result["value"] as JsonArray ?? []).Any(v => Guid.TryParse(v?.GetValue<string>(), out var id) && id == groupId);
    }

    public async Task<bool> HasOneDriveAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            return await _graph.GetOrNullAsync($"users/{userId}/drive?$select=id", ct) is not null;
        }
        catch (CloudException e) when (e.Status is 400 or 403 && e.Code is "BadRequest" or "accessDenied" or "notAllowed")
        {
            // Graph answers this way for accounts that have no licence for OneDrive.
            return false;
        }
    }

    public async Task<UploadedItem> UploadAsync(Guid userId, OneDriveTarget target, string filePath, CancellationToken ct = default)
    {
        var start = target.UnderDocuments
            ? await _graph.GetAsync($"users/{userId}/drive/special/documents?$select=id,parentReference", ct)
            : await _graph.GetAsync($"users/{userId}/drive/root?$select=id,parentReference", ct);
        var driveId = start["parentReference"]!["driveId"]!.GetValue<string>();
        var parentId = start["id"]!.GetValue<string>();
        foreach (var folder in target.Folders)
        {
            parentId = await EnsureFolderAsync(driveId, parentId, folder, ct);
        }

        var size = new FileInfo(filePath).Length;
        for (var attempt = 1; attempt <= 200; attempt++)
        {
            var name = Settings.FileNaming.WithSuffix(target.BaseName, target.Extension, attempt);
            try
            {
                var item = size <= SingleCallLimit
                    ? await UploadSmallAsync(driveId, parentId, name, filePath, ct)
                    : await UploadLargeAsync(driveId, parentId, name, filePath, size, ct);
                return new UploadedItem(driveId, item["id"]!.GetValue<string>(), item["name"]?.GetValue<string>() ?? name);
            }
            catch (CloudException e) when (e.Status == 409)
            {
                // A file with this name is already there. Try the next number.
            }
        }

        throw new CloudException("Too many files with the same name are already in the folder.");
    }

    public async Task<bool> ItemExistsAsync(string driveId, string itemId, CancellationToken ct = default) =>
        await _graph.GetOrNullAsync($"drives/{driveId}/items/{itemId}?$select=id", ct) is not null;

    public async Task SendMailAsync(string sender, IReadOnlyList<string> recipients, string subject, string textBody, CancellationToken ct = default)
    {
        var message = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["subject"] = subject,
                ["body"] = new JsonObject { ["contentType"] = "Text", ["content"] = textBody },
                ["toRecipients"] = new JsonArray([.. recipients.Select(r => (JsonNode)new JsonObject
                {
                    ["emailAddress"] = new JsonObject { ["address"] = r },
                })]),
            },
            ["saveToSentItems"] = false,
        };
        await _graph.PostAsync($"users/{Uri.EscapeDataString(sender)}/sendMail", message, ct);
    }

    private async Task<string> EnsureFolderAsync(string driveId, string parentId, string name, CancellationToken ct)
    {
        var existing = await _graph.GetOrNullAsync($"drives/{driveId}/items/{parentId}:/{Uri.EscapeDataString(name)}?$select=id,folder", ct);
        if (existing is not null)
        {
            if (existing["folder"] is null) throw new CloudException($"\"{name}\" exists in OneDrive but is a file, not a folder.");
            return existing["id"]!.GetValue<string>();
        }

        try
        {
            var created = await _graph.PostAsync(
                $"drives/{driveId}/items/{parentId}/children",
                new JsonObject
                {
                    ["name"] = name,
                    ["folder"] = new JsonObject(),
                    ["@microsoft.graph.conflictBehavior"] = "fail",
                },
                ct);
            return created["id"]!.GetValue<string>();
        }
        catch (CloudException e) when (e.Status == 409)
        {
            // Two scans raced to create it. Read the winner.
            var raced = await _graph.GetAsync($"drives/{driveId}/items/{parentId}:/{Uri.EscapeDataString(name)}?$select=id", ct);
            return raced["id"]!.GetValue<string>();
        }
    }

    private async Task<JsonObject> UploadSmallAsync(string driveId, string parentId, string name, string filePath, CancellationToken ct)
    {
        using var response = await _graph.SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Put,
                _graph.Url($"drives/{driveId}/items/{parentId}:/{Uri.EscapeDataString(name)}:/content?@microsoft.graph.conflictBehavior=fail"))
            {
                Content = FileContent(filePath, 0, null),
            },
            authenticated: true,
            ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
    }

    private async Task<JsonObject> UploadLargeAsync(string driveId, string parentId, string name, string filePath, long size, CancellationToken ct)
    {
        var session = await _graph.PostAsync(
            $"drives/{driveId}/items/{parentId}:/{Uri.EscapeDataString(name)}:/createUploadSession",
            new JsonObject { ["item"] = new JsonObject { ["@microsoft.graph.conflictBehavior"] = "fail" } },
            ct);
        var uploadUrl = session["uploadUrl"]!.GetValue<string>();
        JsonObject? item = null;
        for (long offset = 0; offset < size; offset += ChunkSize)
        {
            var length = Math.Min(ChunkSize, size - offset);
            var from = offset;

            // The upload address carries its own authorisation, so no token is sent.
            using var response = await _graph.SendAsync(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = FileContent(filePath, from, length) };
                    request.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, from + length - 1, size);
                    return request;
                },
                authenticated: false,
                ct);
            if ((int)response.StatusCode is 200 or 201)
            {
                item = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
            }
        }

        return item ?? throw new CloudException("OneDrive accepted every part of the file but did not confirm the upload.");
    }

    private static StreamContent FileContent(string path, long offset, long? length)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        stream.Position = offset;
        var content = new StreamContent(length is null ? stream : new BoundedStream(stream, length.Value));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = length ?? stream.Length - offset;
        return content;
    }

    private static string Escape(string text) => Uri.EscapeDataString(text.Replace("'", "''"));

    private static DirectoryGroup ToGroup(JsonObject g) =>
        new(Guid.Parse(g["id"]!.GetValue<string>()), g["displayName"]?.GetValue<string>() ?? "");

    private static DirectoryUser ToUser(JsonObject u) => new(
        Guid.Parse(u["id"]!.GetValue<string>()),
        u["displayName"]?.GetValue<string>() ?? "",
        u["userPrincipalName"]?.GetValue<string>() ?? "",
        u["accountEnabled"]?.GetValue<bool>() ?? true,
        string.Equals(u["userType"]?.GetValue<string>(), "Guest", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads at most a fixed number of bytes from a stream it owns.</summary>
    private sealed class BoundedStream : Stream
    {
        private readonly Stream inner;
        private readonly long length;
        private long _remaining;

        public BoundedStream(Stream inner, long length)
        {
            this.inner = inner;
            this.length = length;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => length - _remaining; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
