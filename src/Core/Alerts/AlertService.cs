using System.Text;
using Microsoft.Extensions.Logging;
using ScanRelay.Core.Cloud;
using ScanRelay.Core.Settings;

namespace ScanRelay.Core.Alerts;

public enum AlertKind
{
    ScanFailed,
    PushFailed,
    ChangeGuard,
    PrinterUnreachable,
    CertificateExpiring,
    UpdateAvailable,
}

/// <summary>
/// Emails the admin group when something needs attention. Repeated failures of
/// the same kind are grouped, so a Graph outage does not send hundreds of emails.
/// </summary>
public sealed class AlertService(SettingsStore settings, ICloud cloud, TimeProvider time, ILogger<AlertService> logger)
{
    /// <summary>After an email of one kind, more of that kind wait this long and go out together.</summary>
    public static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes(15);

    private readonly Lock _lock = new();
    private readonly Dictionary<AlertKind, DateTimeOffset> _lastSent = [];
    private readonly Dictionary<AlertKind, List<string>> _waiting = [];

    /// <summary>Reports one thing that went wrong. Never throws: an alert that cannot be sent is logged.</summary>
    public async Task RaiseAsync(AlertKind kind, string line, CancellationToken ct = default)
    {
        List<string>? sendNow = null;
        lock (_lock)
        {
            if (_lastSent.TryGetValue(kind, out var last) && time.GetUtcNow() - last < GroupWindow)
            {
                if (!_waiting.TryGetValue(kind, out var list)) _waiting[kind] = list = [];
                list.Add(line);
            }
            else
            {
                _lastSent[kind] = time.GetUtcNow();
                sendNow = [line];
            }
        }

        if (sendNow is not null) await SendAsync(kind, sendNow, ct);
    }

    /// <summary>Sends anything that has waited out its window. Called on a timer.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var due = new List<(AlertKind Kind, List<string> Lines)>();
        lock (_lock)
        {
            foreach (var (kind, lines) in _waiting.ToArray())
            {
                if (lines.Count == 0 || time.GetUtcNow() - _lastSent[kind] < GroupWindow) continue;
                due.Add((kind, lines));
                _waiting.Remove(kind);
                _lastSent[kind] = time.GetUtcNow();
            }
        }

        foreach (var (kind, lines) in due) await SendAsync(kind, lines, ct);
    }

    /// <summary>Sends one email to the admin group. Returns false when alerts are not set up or sending failed.</summary>
    public async Task<bool> SendToAdminsAsync(string subject, string body, CancellationToken ct = default)
    {
        try
        {
            var s = await settings.GetAsync(ct);
            if (string.IsNullOrWhiteSpace(s.AlertSender) || s.AdminGroupId is not { } group) return false;
            var admins = await cloud.GetGroupMembersAsync(group, ct);
            var to = admins.Where(a => a.Enabled && a.UserPrincipalName.Contains('@')).Select(a => a.UserPrincipalName).Distinct().ToList();
            if (to.Count == 0) return false;
            await cloud.SendMailAsync(s.AlertSender, to, subject, body, ct);
            return true;
        }
        catch (Exception e) when (e is CloudException or HttpRequestException)
        {
            logger.LogWarning("An alert email could not be sent: {Reason}", e.Message);
            return false;
        }
    }

    private Task SendAsync(AlertKind kind, List<string> lines, CancellationToken ct)
    {
        var (one, many, where) = kind switch
        {
            AlertKind.ScanFailed => ("A scan failed", "scans failed", "Open Jobs in the admin screen to read the error and re-run."),
            AlertKind.PushFailed => ("A push to a printer failed", "pushes to printers failed", "Open Runs in the admin screen to read the error and re-run."),
            AlertKind.ChangeGuard => ("A sync was stopped by the change guard", "syncs were stopped by the change guard", "Open Runs in the admin screen to approve or cancel."),
            AlertKind.PrinterUnreachable => ("A printer has been unreachable for 24 hours", "printers have been unreachable for 24 hours", "Open Printers in the admin screen to test the connection."),
            AlertKind.CertificateExpiring => ("A certificate expires soon", "certificates expire soon", "Renew it before it expires."),
            _ => ("An update is available", "updates are available", ""),
        };
        var subject = "ScanRelay: " + (lines.Count == 1 ? one : $"{lines.Count} {many}");
        var body = new StringBuilder();
        foreach (var line in lines.Take(50)) body.AppendLine(line);
        if (lines.Count > 50) body.AppendLine($"…and {lines.Count - 50} more.");
        if (where.Length > 0) body.AppendLine().AppendLine(where);
        return SendToAdminsAsync(subject, body.ToString(), ct);
    }
}
