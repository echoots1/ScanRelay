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

/// <summary>Access rights on the folders, the printer account, and the share printers write to.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsSteps
{
    // ---- Access rights ----

    /// <summary>The data folder: the service and administrators, and nobody else.</summary>
    public static void SecureDataFolder(SetupLog log, string dataFolder)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var who in new[] { LocalSystem, Administrators, ServiceSid })
        {
            // Full control includes changing rights, which the service needs for the per-person folders in Inbox.
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
        }

        var info = new DirectoryInfo(dataFolder);
        info.SetAccessControl(security);
        info.Attributes |= FileAttributes.Hidden;
        log.Line($"Set rights on {dataFolder}: inheritance off; full control for SYSTEM, Administrators and {ServiceAccount.AccountName} only. Marked it hidden.");
    }

    /// <summary>Inbox: on top of what it inherits, the printer account may drop files in and nothing more, unless a module needs more.</summary>
    public static void SetInboxRights(SetupLog log, string inbox, SecurityIdentifier printer, InboxRights rights)
    {
        var info = new DirectoryInfo(inbox);
        var security = info.GetAccessControl(AccessControlSections.Access);

        // Start clean, so an earlier printer account or wider rights from a removed module do not linger.
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(rule);
        }

        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(printer, (FileSystemRights)(int)rights.Folders, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(printer, (FileSystemRights)(int)rights.Files, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        info.SetAccessControl(security);
        log.Line($"Set the printer account's rights on {inbox}: {rights.Describe()}. Folders 0x{(int)rights.Folders:X}, files 0x{(int)rights.Files:X}.");
    }

    /// <summary>
    /// The program folder: the service may read and run it, never write to it. Under
    /// Program Files that is already so. A folder elsewhere may have inherited write
    /// access for ordinary users, and is then closed.
    /// </summary>
    public static void SecureProgramFolder(SetupLog log, string folder)
    {
        const FileSystemRights writing = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        const int genericWriteOrAll = 0x40000000 | 0x10000000;

        var info = new DirectoryInfo(folder);
        var rules = info.GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .ToList();
        var trusted = new[] { LocalSystem, Administrators, TrustedInstaller, CreatorOwner };
        var writers = rules
            .Where(r => !trusted.Contains((SecurityIdentifier)r.IdentityReference)
                && ((r.FileSystemRights & writing) != 0 || ((int)r.FileSystemRights & genericWriteOrAll) != 0))
            .Select(r => r.IdentityReference.Value)
            .Distinct()
            .ToList();

        if (writers.Count > 0)
        {
            var closed = new DirectorySecurity();
            closed.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            closed.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
            closed.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
            closed.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
            closed.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.ReadAndExecute, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(closed);
            log.Line($"{folder} could be written to by {string.Join(", ", writers)}. Closed it: full control for SYSTEM and Administrators, read and run for Users and {ServiceAccount.AccountName}.");
            return;
        }

        var through = new[] { ServiceSid, Users, AuthenticatedUsers, Everyone };
        var canRead = rules.Any(r => through.Contains((SecurityIdentifier)r.IdentityReference)
            && (r.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute
            && r.InheritanceFlags == ToEverythingInside
            && !r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly));
        if (canRead)
        {
            log.Line($"{ServiceAccount.AccountName} can already read and run {folder}, and cannot write to it. Rights left as they are.");
            return;
        }

        var security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.ReadAndExecute, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
        log.Line($"Gave {ServiceAccount.AccountName} read and run access to {folder}. It has no write access.");
    }

    /// <summary>
    /// install.json holds the printer password, protected for this machine. Machine
    /// protection alone can be undone by any program on the server, so the file is also closed.
    /// </summary>
    public static void WriteInstallFile(SetupLog log, string installFolder, InstallConfig config)
    {
        var path = Path.Combine(installFolder, InstallConfig.FileName);
        var temporary = path + ".new";
        File.WriteAllText(temporary, InstallFile.Serialize(config));

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(temporary).SetAccessControl(security);
        File.Move(temporary, path, overwrite: true);
        log.Line($"Wrote {path}. Only SYSTEM, Administrators and {ServiceAccount.AccountName} (read only) can open it.");
    }

    // ---- Printer account ----

    public static SecurityIdentifier AccountSid(string name)
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException e)
        {
            throw new SetupException($"Windows could not find the local account \"{name}\" that was just set up.", e);
        }
    }

    /// <summary>
    /// Creates or repairs the printer account and returns its protected password for install.json.
    /// The password is only made anew when the account is new or no stored password can be read back.
    /// </summary>
    public static string EnsurePrinterAccount(SetupLog log, string name, InstallConfig? existing)
    {
        string? stored = null;
        var comment = Native.FindLocalUserComment(name);
        if (comment is not null)
        {
            if (!comment.StartsWith(Native.AccountCommentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupException(
                    $"A local account named \"{name}\" already exists on this server and was not created by ScanRelay. "
                    + "Setup will not take it over. Choose another name for the printer account.");
            }

            if (existing is not null && name.Equals(existing.PrinterAccount, StringComparison.OrdinalIgnoreCase) && CanReadPassword(existing))
            {
                stored = existing.PrinterPasswordProtected;
            }
        }

        if (comment is null)
        {
            var password = PrinterPassword.Generate();
            Native.CreateLocalUser(name, password);
            stored = Core.Printers.PrinterAccountSecret.Protect(password);
            log.Line($"Created the local account {name} with a generated {PrinterPassword.Length}-character password.");
        }
        else if (stored is null)
        {
            var password = PrinterPassword.Generate();
            Native.SetLocalUserPassword(name, password);
            stored = Core.Printers.PrinterAccountSecret.Protect(password);
            log.Warn($"The local account {name} existed but its password was not on record, so a new one was generated. Printers get it with the next address book push.");
        }
        else
        {
            log.Line($"The local account {name} exists and its password is on record. The password was kept.");
        }

        Native.SetLocalUserFlags(name);
        foreach (var group in Native.GetLocalGroups(name))
        {
            Native.RemoveFromLocalGroup(group, name);
            log.Line($"Removed {name} from the local group {group}.");
        }

        var sid = AccountSid(name);
        Native.AddAccountRights(sid, "SeDenyInteractiveLogonRight", "SeDenyRemoteInteractiveLogonRight");
        var rights = Native.GetAccountRights(sid);
        var groups = Native.GetLocalGroups(name);
        log.Line($"{name} ({sid}): password never expires and cannot be changed by the account; local groups: {(groups.Count == 0 ? "none" : string.Join(", ", groups))}; rights: {string.Join(", ", rights)}.");
        if (groups.Count > 0) throw new SetupException($"The account {name} is still in the groups {string.Join(", ", groups)} after setup tried to remove it.");
        return stored;
    }

    public static void DeletePrinterAccount(SetupLog log, string name)
    {
        var comment = Native.FindLocalUserComment(name);
        if (comment is null)
        {
            log.Line($"The local account {name} was already gone.");
            return;
        }

        if (!comment.StartsWith(Native.AccountCommentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn($"The local account {name} was not created by ScanRelay, so it was left alone.");
            return;
        }

        Native.RemoveAllAccountRights(AccountSid(name));
        Native.DeleteLocalUser(name);
        log.Line($"Deleted the local account {name}.");
    }

    private static bool CanReadPassword(InstallConfig config)
    {
        try
        {
            return Core.Printers.PrinterAccountSecret.Read(config).Length > 0;
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }

    // ---- Share ----

    public static void ShareInbox(SetupLog log, string shareName, string inbox, SecurityIdentifier printer, IEnumerable<string> foldersSetupOwns)
    {
        if (Native.FindSharePath(shareName) is { } current)
        {
            if (!foldersSetupOwns.Append(inbox).Any(f => WinPath.Same(f, current)))
            {
                throw new SetupException($"A share named {shareName} already exists on this server for {current}. Setup will not replace it. Choose another share name.");
            }

            Native.DeleteShare(shareName);
            log.Line($"Removed the existing share {shareName} so it can be made again.");
        }

        Native.CreateShare(shareName, inbox, "ScanRelay: printers save scans here.", printer);
        log.Line($"Shared {inbox} as \\\\{Environment.MachineName}\\{shareName}. Only {printer} (the printer account) has access to the share: Change.");
    }

    /// <summary>Removes a share, but only one that points at a folder setup made.</summary>
    public static void RemoveShare(SetupLog log, string shareName, params string[] foldersSetupOwns)
    {
        var current = Native.FindSharePath(shareName);
        if (current is null)
        {
            log.Line($"The share {shareName} was already gone.");
        }
        else if (!foldersSetupOwns.Any(f => WinPath.Same(f, current)))
        {
            log.Warn($"The share {shareName} points at {current}, which is not ScanRelay's, so it was left alone.");
        }
        else
        {
            Native.DeleteShare(shareName);
            log.Line($"Removed the share {shareName}.");
        }
    }
}
