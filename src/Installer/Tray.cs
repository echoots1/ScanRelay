using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Sockets;

namespace ScanRelay.Installer;

/// <summary>
/// Everyday use of an installed ScanRelay: the icon by the clock, and what the desktop icon
/// does. None of this changes the install. Starting and stopping the service needs an
/// administrator, so those run this same program again, elevated, and Windows asks.
/// </summary>
internal static class Everyday
{
    public const string Title = "ScanRelay";

    private static string Self => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ScanRelaySetup.exe");

    /// <summary>The admin screen as opened on this server, from the install settings beside this program.</summary>
    public static string? AdminScreenUrl()
    {
        // Setup leaves the address where anyone may read it. The install settings themselves are closed to all but administrators.
        if (ServiceControl.AdminScreenUrl() is { } url) return url;
        try
        {
            return InstallFile.Read(AppContext.BaseDirectory) is { Url.Length: > 0 } config ? config.LocalUrl() : null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Starts, stops or restarts the service. When this process is not an administrator, Windows asks and an elevated copy does it.</summary>
    public static string? ControlService(string verb, bool elevated)
    {
        if (elevated) return ServiceControl.Run(verb);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Self, $"/service {verb}") { UseShellExecute = true, Verb = "runas" });
            if (process is null) return "Windows did not start the administrator step.";
            process.WaitForExit();
            return process.ExitCode == 0 ? null : $"The ScanRelay service could not {verb}. Look in Windows Event Viewer, under Application, for entries from ScanRelay.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return $"An administrator is needed to {verb} the ScanRelay service, and none was given.";
        }
    }

    /// <summary>Opens the admin screen, starting the service first when it is not running. Returns what went wrong, or null.</summary>
    public static string? Open(bool elevated)
    {
        var state = ServiceControl.State();
        if (state == ServiceState.NotInstalled) return "ScanRelay is not installed on this computer.";
        if (state == ServiceState.Stopped && ControlService("start", elevated) is { } problem) return problem;
        if (AdminScreenUrl() is not { } url) return "ScanRelay's install settings could not be read, so its address is not known. Run setup and choose Repair.";

        // Running is not yet listening. Give the admin screen a little time to answer before the browser asks.
        var uri = new Uri(url);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(45))
        {
            try
            {
                using var probe = new TcpClient();
                if (probe.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromSeconds(2)) && probe.Connected) break;
            }
            catch (Exception e) when (e is SocketException or AggregateException)
            {
            }

            Thread.Sleep(500);
        }

        WizardForm.OpenInBrowser(url);
        return null;
    }

    /// <summary>Shows the icon by the clock if setup was asked for one and this person's session does not have it yet.</summary>
    public static void EnsureTray()
    {
        try
        {
            if (!ServiceControl.TrayIconWanted()) return;
            if (Mutex.TryOpenExisting(TrayIcon.MutexName, out var running))
            {
                running.Dispose();
                return;
            }

            Process.Start(new ProcessStartInfo(Self, "/tray") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            // The icon is a convenience. Opening the admin screen goes ahead without it.
        }
    }
}

/// <summary>The icon by the clock.</summary>
internal sealed class TrayIcon : ApplicationContext
{
    /// <summary>One icon per signed-in person.</summary>
    public const string MutexName = @"Local\ScanRelayTrayIcon";

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _start;
    private readonly ToolStripMenuItem _stop;
    private readonly ToolStripMenuItem _restart;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<ServiceState, Icon> _icons = [];
    private readonly bool _elevated;
    private ServiceState? _shown;
    private bool _busy;

    public TrayIcon(bool elevated)
    {
        _elevated = elevated;
        var open = new ToolStripMenuItem("&Open ScanRelay", null, (_, _) => Do(() => Everyday.Open(_elevated)));
        open.Font = new Font(open.Font, FontStyle.Bold);
        _start = new ToolStripMenuItem("&Start the service", null, (_, _) => Do(() => Everyday.ControlService("start", _elevated)));
        _stop = new ToolStripMenuItem("S&top the service", null, (_, _) => Do(() => Everyday.ControlService("stop", _elevated)));
        _restart = new ToolStripMenuItem("&Restart the service", null, (_, _) => Do(() => Everyday.ControlService("restart", _elevated)));
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([open, new ToolStripSeparator(), _start, _stop, _restart, new ToolStripSeparator(),
            new ToolStripMenuItem("&Hide this icon until next sign-in", null, (_, _) => ExitThread())]);

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => Do(() => Everyday.Open(_elevated));
        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var state = ServiceControl.State();
        if (state == _shown) return;
        _shown = state;
        _icon.Icon = IconFor(state);
        _icon.Text = state switch
        {
            ServiceState.Running => "ScanRelay is running",
            ServiceState.Stopped => "ScanRelay is stopped",
            ServiceState.Changing => "ScanRelay is starting or stopping",
            _ => "ScanRelay is not installed",
        };
        _start.Enabled = state == ServiceState.Stopped;
        _stop.Enabled = state == ServiceState.Running;
        _restart.Enabled = state == ServiceState.Running;
    }

    /// <summary>Runs one action off the window's own thread, so the menu never freezes while the service starts.</summary>
    private async void Do(Func<string?> action)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var problem = await Task.Run(action);
            if (problem is not null) _icon.ShowBalloonTip(8000, Everyday.Title, problem, ToolTipIcon.Warning);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            _icon.ShowBalloonTip(8000, Everyday.Title, e.Message, ToolTipIcon.Warning);
        }
        finally
        {
            _busy = false;
            _shown = null;
            Refresh();
        }
    }

    /// <summary>The program's own icon with a dot in the corner: green running, red stopped, amber in between.</summary>
    private Icon IconFor(ServiceState state)
    {
        if (_icons.TryGetValue(state, out var made)) return made;
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var own = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath) ?? SystemIcons.Application;
            using var sized = new Icon(own, 32, 32);
            g.DrawIcon(sized, new Rectangle(0, 0, 32, 32));
            var colour = state switch
            {
                ServiceState.Running => Color.FromArgb(46, 160, 67),
                ServiceState.Changing => Color.FromArgb(219, 154, 4),
                _ => Color.FromArgb(207, 34, 46),
            };
            using var edge = new SolidBrush(Color.White);
            using var dot = new SolidBrush(colour);
            g.FillEllipse(edge, 17, 17, 15, 15);
            g.FillEllipse(dot, 19, 19, 11, 11);
        }

        return _icons[state] = Icon.FromHandle(bitmap.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}
