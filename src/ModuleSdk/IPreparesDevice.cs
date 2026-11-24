namespace ScanRelay.ModuleSdk;

/// <summary>
/// For a module that has to read something from the printer once before it can push,
/// such as a stored password only the printer can produce. An adapter implements this
/// next to <see cref="IDeviceAdapter"/>. The Printers page then shows a button for it.
/// </summary>
public interface IPreparesDevice
{
    /// <summary>The text on the button, such as "Read the address book from the printer".</summary>
    string PrepareLabel { get; }

    /// <summary>One or two sentences shown beside the button: what to do on the printer first, and what the button does.</summary>
    string PrepareHint { get; }

    /// <summary>Reads what the module needs. This must not change anything on the printer.</summary>
    Task<PrepareResult> PrepareAsync(Device device, CancellationToken cancellationToken = default);
}

/// <param name="Values">
/// What the module wants kept. The service stores these encrypted with the printer, never
/// shows them, and hands them back in <see cref="Device.Options"/> on every later call.
/// </param>
/// <param name="Message">What to tell the admin: what was read, or why it could not be.</param>
public sealed record PrepareResult(bool Succeeded, IReadOnlyDictionary<string, string> Values, string? Message);
