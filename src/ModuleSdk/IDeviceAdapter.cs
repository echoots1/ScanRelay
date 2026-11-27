namespace ScanRelay.ModuleSdk;

/// <summary>
/// The contract a printer module implements. One adapter covers one printer family.
/// Installed modules are built against these shapes, so they are not changed: see ModuleSdkInfo.Version.
/// </summary>
public interface IDeviceAdapter
{
    /// <summary>The module id, matching <c>id</c> in the module data.</summary>
    string Id { get; }

    /// <summary>Checks the printer is reachable and reports its model and firmware.</summary>
    Task<ProbeResult> ProbeAsync(Device device, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the entries in the scope this module manages.
    /// Returns null when the printer cannot be read back.
    /// </summary>
    Task<IReadOnlyList<Entry>?> ReadManagedAsync(Device device, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the printer's managed entries equal to <paramref name="wanted"/>.
    /// Must be safe to run twice, and must never touch entries outside the managed scope.
    /// </summary>
    Task<ApplyResult> ApplyAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken cancellationToken = default);

    /// <summary>Builds a file an administrator can apply by hand when a push fails.</summary>
    Task<ImportFile> ExportAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken cancellationToken = default);
}
