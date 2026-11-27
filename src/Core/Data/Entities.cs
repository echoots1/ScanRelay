namespace ScanRelay.Core.Data;

// The schema. All times are UTC. A change here needs a migration.

/// <summary>One setting from the Settings page. Secrets hold the encrypted value.</summary>
public class Setting
{
    public required string Key { get; set; }
    public string? Value { get; set; }
    public bool IsSecret { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public enum PersonSource
{
    Group,
    Manual,
}

/// <summary>Someone who can scan. The id is their directory user id, which is also their Inbox folder name.</summary>
public class Person
{
    public Guid Id { get; set; }
    public required string DisplayName { get; set; }
    public required string UserPrincipalName { get; set; }

    /// <summary>Their mailbox address, when they have one.</summary>
    public string? Email { get; set; }
    public PersonSource Source { get; set; }

    /// <summary>Excluded on the People page. Kept through every sync.</summary>
    public bool Excluded { get; set; }

    /// <summary>For a manual person: on every printer, or only those in <see cref="Printers"/>.</summary>
    public bool OnAllPrinters { get; set; } = true;

    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Set when the person leaves the list. Their folder goes once it is empty; the row is kept, so old jobs still name them.</summary>
    public DateTime? RemovedUtc { get; set; }

    public List<PersonPrinter> Printers { get; set; } = [];
}

/// <summary>A person assigned to one chosen printer, on top of whatever group that printer lists.</summary>
public class PersonPrinter
{
    public Guid PersonId { get; set; }
    public int PrinterId { get; set; }
    public Person Person { get; set; } = null!;
    public Printer Printer { get; set; } = null!;
}

public class Printer
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Address { get; set; }
    public string? Model { get; set; }

    /// <summary>The module that drives this printer.</summary>
    public required string ModuleId { get; set; }

    /// <summary>This printer's own smaller group, if it has one. Null means the default sync group.</summary>
    public Guid? SyncGroupId { get; set; }

    /// <summary>List only the people assigned to this printer one by one. No group is used, not even the default.</summary>
    public bool AssignedOnly { get; set; }

    /// <summary>That group's name, as it was when chosen. For showing only.</summary>
    public string? SyncGroupName { get; set; }

    /// <summary>Per-printer values that override the module data, as JSON.</summary>
    public string OptionsJson { get; set; } = "{}";

    public string? AdminUser { get; set; }

    /// <summary>The printer's admin password, encrypted. Never shown again.</summary>
    public byte[]? AdminPasswordProtected { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? LastReachableUtc { get; set; }

    public List<PrinterEntry> Entries { get; set; } = [];
}

/// <summary>What the database says a printer holds now. Compared with the wanted list on each sync.</summary>
public class PrinterEntry
{
    public int PrinterId { get; set; }
    public Guid PersonId { get; set; }
    public required string DisplayName { get; set; }
    public string? Email { get; set; }
    public DateTime PushedUtc { get; set; }
    public Printer Printer { get; set; } = null!;
}

public enum JobStatus
{
    Waiting,
    Uploading,
    Uploaded,
    Failed,
}

/// <summary>One scan. The id is also the file's name in Store.</summary>
public class Job
{
    public Guid Id { get; set; }

    /// <summary>The Inbox folder the file arrived in. Normally a user id.</summary>
    public required string FolderName { get; set; }

    /// <summary>Null when the folder matches no known person.</summary>
    public Guid? PersonId { get; set; }

    public required string OriginalFileName { get; set; }

    /// <summary>The name the file was given in OneDrive.</summary>
    public string? UploadedFileName { get; set; }

    /// <summary>The OneDrive item id, so a re-run does not create a second copy.</summary>
    public string? DriveItemId { get; set; }

    public long SizeBytes { get; set; }
    public JobStatus Status { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public DateTime DetectedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Set when retention deletes the file from Store. Re-run is offered only while this is null.</summary>
    public DateTime? FileDeletedUtc { get; set; }
}

public enum RunKind
{
    Sync,
    Preview,
    DriftCheck,
}

public enum RunTrigger
{
    Schedule,
    Manual,
}

public enum RunStatus
{
    Running,
    Succeeded,
    Failed,

    /// <summary>Stopped by the change guard and waiting for an Admin.</summary>
    Held,
    Cancelled,
}

public class Run
{
    public int Id { get; set; }
    public RunKind Kind { get; set; }
    public RunTrigger Trigger { get; set; }
    public RunStatus Status { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public int PeopleAdded { get; set; }
    public int PeopleRemoved { get; set; }
    public string? Detail { get; set; }

    /// <summary>Who started, approved or cancelled the run. Null for scheduled runs.</summary>
    public string? Actor { get; set; }

    public List<Push> Pushes { get; set; } = [];
}

public enum PushStatus
{
    Pending,
    Succeeded,
    Failed,

    /// <summary>The printer's module cannot be used (not installed, not for this app, or not trusted), so pushes to the printer are paused.</summary>
    Paused,
}

/// <summary>One push of a full wanted list to one printer.</summary>
public class Push
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public int PrinterId { get; set; }
    public PushStatus Status { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public string? Error { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public Run Run { get; set; } = null!;
    public Printer Printer { get; set; } = null!;
}

/// <summary>Who changed what, and when.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }

    /// <summary>The directory user id of who did it. Null for the installer and first-run setup.</summary>
    public Guid? ActorId { get; set; }
    public required string ActorName { get; set; }
    public required string Action { get; set; }
    public string? Target { get; set; }
    public string? Detail { get; set; }
}
