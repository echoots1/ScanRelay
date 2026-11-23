using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ScanRelay.MockCloud;

public sealed record MockUser(Guid Id, string DisplayName, string Upn)
{
    public bool Enabled { get; set; } = true;
    public bool Guest { get; set; }
    public bool HasDrive { get; set; } = true;

    /// <summary>Their mailbox address. Null for an account with no mailbox.</summary>
    public string? Mail { get; set; } = Upn;

    /// <summary>The OneDrive exists but SharePoint has locked it, as it does for some blocked or unlicensed accounts.</summary>
    public bool DriveLocked { get; set; }
    public string DriveId => "drive-" + Id.ToString("N");
}

public sealed class MockGroup(Guid id, string name)
{
    public Guid Id { get; } = id;
    public string DisplayName { get; set; } = name;
    public HashSet<Guid> Members { get; } = [];
    public HashSet<Guid> NestedGroups { get; } = [];
}

public sealed class MockItem
{
    public required string Id { get; init; }
    public required string DriveId { get; init; }
    public string? ParentId { get; init; }
    public required string Name { get; set; }
    public bool IsFolder { get; init; }
    public byte[] Content { get; set; } = [];
}

public sealed class MockApplication
{
    public string ObjectId { get; } = Guid.NewGuid().ToString();
    public string AppId { get; } = Guid.NewGuid().ToString();
    public string DisplayName { get; set; } = "";
    public List<string> RedirectUris { get; set; } = [];
    public List<byte[]> Certificates { get; set; } = [];
    public List<string> RequestedRoleIds { get; set; } = [];
    public bool HasServicePrincipal { get; set; }
    public bool ConsentGranted { get; set; }
}

public sealed record SentMail(string Sender, IReadOnlyList<string> To, string Subject, string Body);

/// <summary>Everything the mock Microsoft 365 holds. Tests read and change it directly.</summary>
public sealed class MockCloudState
{
    public string TenantId { get; } = Guid.NewGuid().ToString();
    public string OrganizationName { get; set; } = "Example Organisation";
    public RSA SigningKey { get; } = RSA.Create(2048);
    public string KeyId { get; } = "mock-key-1";

    public ConcurrentDictionary<Guid, MockUser> Users { get; } = new();
    public ConcurrentDictionary<Guid, MockGroup> Groups { get; } = new();
    public ConcurrentDictionary<string, MockItem> Items { get; } = new();
    public ConcurrentDictionary<string, MockApplication> Applications { get; } = new();
    public ConcurrentQueue<SentMail> Outbox { get; } = new();
    public ConcurrentDictionary<string, (string DriveId, string ParentId, string Name, MemoryStream Data)> UploadSessions { get; } = new();
    public ConcurrentDictionary<string, bool> DeviceCodes { get; } = new();
    public ConcurrentDictionary<string, (Guid User, string Nonce, string ClientId)> AuthCodes { get; } = new();

    /// <summary>The latest release on the mock GitHub: its tag, title and notes. Null means no releases.</summary>
    public (string Tag, string Name, string Body)? Release { get; set; }

    /// <summary>The files attached to the latest release.</summary>
    public ConcurrentDictionary<string, byte[]> ReleaseAssets { get; } = new();

    /// <summary>The next N tokens for the service carry no permissions, as Entra's can for a few minutes after consent.</summary>
    public int TokensWithoutPermissions;

    /// <summary>The person the next device code or browser sign-in signs in as.</summary>
    public Guid? SignInAs { get; set; }

    /// <summary>Approve device codes as soon as they are polled.</summary>
    public bool AutoApproveDeviceCodes { get; set; } = true;

    /// <summary>Graph answers this status to the next N requests, then recovers.</summary>
    public int FailNextGraphRequests;
    public int FailStatus { get; set; } = 503;

    /// <summary>Every Graph request seen, as "METHOD path".</summary>
    public ConcurrentQueue<string> GraphRequests { get; } = new();

    public MockUser AddUser(string name, string? upn = null, bool hasDrive = true, bool enabled = true, bool guest = false)
    {
        var user = new MockUser(Guid.NewGuid(), name, upn ?? name.ToLowerInvariant().Replace(' ', '.') + "@example.com")
        {
            HasDrive = hasDrive,
            Enabled = enabled,
            Guest = guest,
        };
        Users[user.Id] = user;
        return user;
    }

    public MockGroup AddGroup(string name, params MockUser[] members)
    {
        var group = new MockGroup(Guid.NewGuid(), name);
        foreach (var m in members) group.Members.Add(m.Id);
        Groups[group.Id] = group;
        return group;
    }

    public IEnumerable<MockUser> TransitiveMembers(MockGroup group, HashSet<Guid>? seen = null)
    {
        seen ??= [];
        if (!seen.Add(group.Id)) yield break;
        foreach (var id in group.Members)
        {
            if (Users.TryGetValue(id, out var user)) yield return user;
        }

        foreach (var nested in group.NestedGroups)
        {
            if (!Groups.TryGetValue(nested, out var g)) continue;
            foreach (var user in TransitiveMembers(g, seen)) yield return user;
        }
    }

    public MockItem Root(string driveId) => Items.GetOrAdd(driveId + "!root", id => new MockItem { Id = id, DriveId = driveId, Name = "root", IsFolder = true });

    /// <summary>The Documents folder, deliberately given a translated name.</summary>
    public MockItem Documents(string driveId)
    {
        var root = Root(driveId);
        return Items.GetOrAdd(driveId + "!documents", id => new MockItem { Id = id, DriveId = driveId, ParentId = root.Id, Name = "Dokumente", IsFolder = true });
    }

    public MockItem? Child(string parentId, string name) =>
        Items.Values.FirstOrDefault(i => i.ParentId == parentId && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<MockItem> Children(string parentId) => Items.Values.Where(i => i.ParentId == parentId);

    /// <summary>The files in a person's OneDrive under a path such as "Dokumente/Scans".</summary>
    public IReadOnlyList<MockItem> Files(MockUser user, string path)
    {
        Documents(user.DriveId);
        var current = Root(user.DriveId);
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Child(current.Id, part);
            if (next is null) return [];
            current = next;
        }

        return [.. Children(current.Id).Where(i => !i.IsFolder).OrderBy(i => i.Name)];
    }

    public MockItem AddItem(string driveId, string parentId, string name, bool folder, byte[]? content = null)
    {
        var item = new MockItem
        {
            Id = driveId + "!" + Guid.NewGuid().ToString("N"),
            DriveId = driveId,
            ParentId = parentId,
            Name = name,
            IsFolder = folder,
            Content = content ?? [],
        };
        Items[item.Id] = item;
        return item;
    }
}
