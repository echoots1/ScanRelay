using System.Runtime.Versioning;

namespace ScanRelay.Core.Printers;

/// <summary>A printer installed on this Windows server, as Windows reports it.</summary>
public sealed record InstalledPrinter(string Name, string Address, string Model);

/// <summary>Lists the printers installed on this server. It never scans the network.</summary>
public interface IInstalledPrinters
{
    IReadOnlyList<InstalledPrinter> List();
}

public sealed class InstalledPrinters : IInstalledPrinters
{
    public IReadOnlyList<InstalledPrinter> List() => OperatingSystem.IsWindows() ? ListWindows() : [];

    [SupportedOSPlatform("windows")]
    private static List<InstalledPrinter> ListWindows()
    {
        try
        {
            var ports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var search = new System.Management.ManagementObjectSearcher("SELECT Name, HostAddress FROM Win32_TCPIPPrinterPort"))
            {
                foreach (var port in search.Get())
                {
                    using (port)
                    {
                        if (port["Name"] is string name && port["HostAddress"] is string host) ports[name] = host;
                    }
                }
            }

            var printers = new List<InstalledPrinter>();
            using (var search = new System.Management.ManagementObjectSearcher("SELECT Name, DriverName, PortName FROM Win32_Printer"))
            {
                foreach (var printer in search.Get())
                {
                    using (printer)
                    {
                        var name = printer["Name"] as string ?? "";
                        var portName = printer["PortName"] as string ?? "";
                        var address = ports.GetValueOrDefault(portName) ?? (System.Net.IPAddress.TryParse(portName.Replace("IP_", ""), out var ip) ? ip.ToString() : "");

                        // Software printers have no address to push to.
                        if (address.Length == 0 && !portName.StartsWith("WSD", StringComparison.OrdinalIgnoreCase)) continue;
                        printers.Add(new InstalledPrinter(name, address, printer["DriverName"] as string ?? ""));
                    }
                }
            }

            return [.. printers.OrderBy(p => p.Name)];
        }
        catch (Exception e) when (e is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
