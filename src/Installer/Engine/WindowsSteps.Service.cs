using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using ScanRelay.Core.Install;

namespace ScanRelay.Installer;

/// <summary>The Windows service: registering, starting, stopping and removing it.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsSteps
{
    // ---- Service ----

    public static bool ServiceExists()
    {
        var services = ServiceController.GetServices();
        try
        {
            return services.Any(s => s.ServiceName.Equals(ServiceAccount.ServiceName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var s in services) s.Dispose();
        }
    }

    public static void StopService(SetupLog log)
    {
        if (!ServiceExists())
        {
            log.Line("The ScanRelay service is not registered, so there is nothing to stop.");
            return;
        }

        using var service = new ServiceController(ServiceAccount.ServiceName);
        if (service.Status == ServiceControllerStatus.Stopped)
        {
            log.Line("The ScanRelay service is already stopped.");
            return;
        }

        try
        {
            ServiceControl.StopAndWait(service);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            throw new SetupException($"The ScanRelay service did not stop within {ServiceControl.Patience.TotalSeconds:0} seconds. Stop it in Services, then run setup again.");
        }
        catch (InvalidOperationException e)
        {
            throw new SetupException($"The ScanRelay service could not be stopped: {e.InnerException?.Message ?? e.Message}", e);
        }

        log.Line("Stopped the ScanRelay service.");
    }

    /// <summary>
    /// Windows reports the service as stopped a little before its program has finished closing,
    /// and until it has, its files cannot be replaced. This waits for the program itself to be gone.
    /// </summary>
    public static void WaitForServiceProgramToClose(SetupLog log, string installFolder)
    {
        var exe = Path.Combine(installFolder, ServiceExe);
        var watch = Stopwatch.StartNew();
        var waited = false;
        while (true)
        {
            var running = RunningFrom(exe);

            if (running.Count == 0) break;
            waited = true;
            if (watch.Elapsed > TimeSpan.FromSeconds(30))
            {
                // The service has been told to stop and said it had. What is left is a program that will not go by itself.
                foreach (var process in running) TryEnd(process);

                log.Line("The ScanRelay service's program was still running 30 seconds after the service stopped, so it was ended.");
            }

            foreach (var process in running) process.Dispose();
            Thread.Sleep(500);
        }

        if (waited) log.Line($"Waited {watch.Elapsed.TotalSeconds:0.0} seconds for the ScanRelay service's program to finish closing.");
    }

    public static void RegisterService(SetupLog log, string installFolder)
    {
        var exe = Path.Combine(installFolder, ServiceExe);

        // The inner quotes are stored with the path, so a folder with spaces in it cannot be misread.
        var binPath = "\"\\\"" + exe + "\\\"\"";
        var name = ServiceAccount.ServiceName;
        if (!ServiceExists())
        {
            // Created as start-on-demand under the default account and then switched below.
            // If the switch fails, nothing is left that would start by itself as SYSTEM.
            Cmd.Run(log, "sc.exe", $"create {name} binPath= {binPath} start= demand DisplayName= {Cmd.Quote(name)}");
            log.Line($"Created the Windows service {name}.");
        }

        var configure = $"config {name} binPath= {binPath} obj= {Cmd.Quote(ServiceAccount.AccountName)} start= delayed-auto DisplayName= {Cmd.Quote(name)}";
        if (!Cmd.TryRun(log, "sc.exe", configure).Succeeded)
        {
            // Some Windows versions want an empty password spelled out for a virtual account.
            Cmd.Run(log, "sc.exe", configure + " password= \"\"");
        }

        Cmd.Run(log, "sc.exe", $"description {name} {Cmd.Quote(ServiceAccount.Description)}");
        Cmd.Run(log, "sc.exe", $"failure {name} reset= 86400 actions= restart/5000/restart/30000/restart/60000");
        log.Line($"The service runs {exe} as {ServiceAccount.AccountName}, starts automatically (delayed) and restarts after a failure.");
    }

    public static void DeleteService(SetupLog log)
    {
        if (!ServiceExists())
        {
            log.Line("The ScanRelay service was not registered.");
            return;
        }

        StopService(log);
        Cmd.Run(log, "sc.exe", $"delete {ServiceAccount.ServiceName}");
        for (var i = 0; i < 30 && ServiceExists(); i++) Thread.Sleep(1000);
        if (ServiceExists()) log.Warn("Windows has marked the ScanRelay service for removal, but something still has it open (often the Services window). It goes when that closes or the server restarts.");
        else log.Line("Removed the ScanRelay service.");
    }

    /// <summary>Starts the service and waits for the admin screen's health check to answer.</summary>
    public static async Task StartServiceAndWaitAsync(SetupLog log, SetupAnswers answers, CancellationToken ct)
    {
        using var service = new ServiceController(ServiceAccount.ServiceName);
        try
        {
            ServiceControl.StartIfNeeded(service);
        }
        catch (InvalidOperationException e)
        {
            throw new SetupException($"The ScanRelay service could not be started: {e.InnerException?.Message ?? e.Message} {WhereToLook(answers)}", e);
        }

        log.Line("Started the ScanRelay service. Waiting for the admin screen to answer.");
        var url = answers.LocalUrl + "/health";

        // The check is to this machine itself, where a self-signed certificate is expected
        // and the name localhost may not be on a certificate the admin picked.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var last = "it has not answered";
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < ServiceControl.Patience)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    log.Line($"{url} answered 200 after {watch.Elapsed.TotalSeconds:0} seconds.");
                    return;
                }

                last = $"it answered {(int)response.StatusCode}: {Truncate(await response.Content.ReadAsStringAsync(ct))}";
            }
            catch (HttpRequestException e)
            {
                last = e.InnerException?.Message ?? e.Message;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = "the request timed out";
            }

            service.Refresh();
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                throw new SetupException($"The ScanRelay service started and then stopped again. {WhereToLook(answers)}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new SetupException($"The ScanRelay service is running, but {url} did not answer 200 within {ServiceControl.Patience.TotalSeconds:0} seconds. Last result: {last}. {WhereToLook(answers)}");
    }

    private static string WhereToLook(SetupAnswers answers) =>
        $"Look in {Path.Combine(answers.DataFolder, "Logs")} and in the Windows Application event log for the reason.";

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "...";
}
