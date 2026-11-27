using System.Runtime.Versioning;
using System.ServiceProcess;

namespace ScanRelay.Installer;

public enum ServiceState
{
    NotInstalled,
    Stopped,
    Changing,
    Running,
}

/// <summary>Looking at the ScanRelay service and starting or stopping it, for the tray icon and the desktop icon.</summary>
[SupportedOSPlatform("windows")]
public static class ServiceControl
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    /// <summary>Anyone may ask this. Only starting and stopping need an administrator.</summary>
    public static ServiceState State()
    {
        try
        {
            using var service = new ServiceController(ServiceAccount.ServiceName);
            return service.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                _ => ServiceState.Changing,
            };
        }
        catch (InvalidOperationException)
        {
            return ServiceState.NotInstalled;
        }
    }

    /// <summary>Starts, stops or restarts the service and waits for it. Needs an administrator. Returns null when it worked, or what went wrong.</summary>
    public static string? Run(string verb)
    {
        try
        {
            using var service = new ServiceController(ServiceAccount.ServiceName);
            if (verb is "stop" or "restart")
            {
                service.Refresh();
                if (service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)) service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, Patience);
            }

            if (verb is "start" or "restart")
            {
                service.Refresh();
                if (service.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)) service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, Patience);
            }

            return null;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return $"The ScanRelay service did not {verb} within {Patience.TotalSeconds:0} seconds. Look in Windows Event Viewer, under Application, for entries from ScanRelay.";
        }
        catch (InvalidOperationException e)
        {
            return $"The ScanRelay service could not {verb}: {e.InnerException?.Message ?? e.Message}";
        }
    }

    /// <summary>The admin screen's address on this server, as setup recorded it, or null when ScanRelay is not installed.</summary>
    public static string? AdminScreenUrl() => WindowsSteps.ReadLocalUrl();

    /// <summary>Whether setup was asked to show the icon by the clock.</summary>
    public static bool TrayIconWanted() => WindowsSteps.TrayIconWanted();

    public static int ExitCode(ServiceState state) => state switch
    {
        ServiceState.Running => 0,
        ServiceState.NotInstalled => 4,
        _ => 3,
    };
}
