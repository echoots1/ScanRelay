namespace ScanRelay.Core;

/// <summary>
/// The app's one data folder and what lives in it. Only the service and
/// administrators can open it; only <see cref="Inbox"/> is shared.
/// </summary>
public sealed class DataFolder
{
    public DataFolder(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>Where printers write. One folder per person, named with their user id.</summary>
    public string Inbox => Path.Combine(Root, "Inbox");

    /// <summary>Every processed file, uploaded or failed, named by job id.</summary>
    public string Store => Path.Combine(Root, "Store");

    public string Logs => Path.Combine(Root, "Logs");

    public string Database => Path.Combine(Root, "scanrelay.db");

    /// <summary>Creates any folder that is missing. The installer normally creates them, with the right access.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Inbox);
        Directory.CreateDirectory(Store);
        Directory.CreateDirectory(Logs);
    }
}
