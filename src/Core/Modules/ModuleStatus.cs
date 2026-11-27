namespace ScanRelay.Core.Modules;

/// <summary>The two kinds of module, as written in module data and in the record of what is installed.</summary>
public static class ModuleStatus
{
    /// <summary>From the project, and signed by it.</summary>
    public const string Official = "official";

    /// <summary>From anywhere else. Not checked by the project.</summary>
    public const string Custom = "custom";
}
