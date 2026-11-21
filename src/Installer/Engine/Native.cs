using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ScanRelay.Installer;

/// <summary>
/// Every direct call into Windows, in one place. These are used where the command-line
/// tools would mean reading translated text, or would add the account to a group.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Native
{
    private const int NerrSuccess = 0;
    private const int NerrNetNameNotFound = 2310;
    private const int NerrUserNotFound = 2221;
    private const int NerrUserExists = 2224;
    private const int NerrPasswordTooShort = 2245;
    private const int ErrorMemberNotInAlias = 1377;
    private const int ErrorNoSuchAlias = 1376;

    private const uint UserPrivUser = 1;
    private const uint UfScript = 0x0001;
    private const uint UfPasswdCantChange = 0x0040;
    private const uint UfNormalAccount = 0x0200;
    private const uint UfDontExpirePasswd = 0x10000;

    /// <summary>Marks the account as one setup made, so setup never takes over an account it did not create.</summary>
    public const string AccountCommentPrefix = "ScanRelay printer account";
    private const string AccountComment = AccountCommentPrefix + ". Printers use it to save scans. Created by ScanRelay setup.";

    // ---- Local accounts ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        public string Name;
        public string? Password;
        public uint PasswordAge;
        public uint Priv;
        public string? HomeDir;
        public string? Comment;
        public uint Flags;
        public string? ScriptPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1003
    {
        public string Password;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo1008
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1007
    {
        public string Comment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupUsersInfo0
    {
        public IntPtr Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMembersInfo3
    {
        public string DomainAndName;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserAdd(string? server, int level, ref UserInfo1 info, out int parameterError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(string? server, string user, int level, ref UserInfo1003 info, out int parameterError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(string? server, string user, int level, ref UserInfo1008 info, out int parameterError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(string? server, string user, int level, ref UserInfo1007 info, out int parameterError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserDel(string? server, string user);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetLocalGroups(string? server, string user, int level, int flags, out IntPtr buffer, int preferredLength, out int entriesRead, out int totalEntries);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupDelMembers(string? server, string group, int level, ref LocalGroupMembersInfo3 members, int count);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>The comment on a local account, or null when there is no such account.</summary>
    public static string? FindLocalUserComment(string name)
    {
        var code = NetUserGetInfo(null, name, 1, out var buffer);
        if (code == NerrUserNotFound) return null;
        Check(code, $"reading the local account \"{name}\"");
        try
        {
            return Marshal.PtrToStructure<UserInfo1>(buffer).Comment ?? "";
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    /// <summary>Creates a plain local account. Unlike <c>net user /add</c>, this puts it in no local group.</summary>
    public static void CreateLocalUser(string name, string password)
    {
        var info = new UserInfo1
        {
            Name = name,
            Password = password,
            Priv = UserPrivUser,
            Comment = AccountComment,
            Flags = UfScript | UfNormalAccount | UfDontExpirePasswd | UfPasswdCantChange,
        };
        Check(NetUserAdd(null, 1, ref info, out _), $"creating the local account \"{name}\"");
    }

    public static void SetLocalUserPassword(string name, string password)
    {
        var info = new UserInfo1003 { Password = password };
        Check(NetUserSetInfo(null, name, 1003, ref info, out _), $"setting the password of the local account \"{name}\"");
    }

    /// <summary>Password never expires, and the account cannot change it.</summary>
    public static void SetLocalUserFlags(string name)
    {
        var flags = new UserInfo1008 { Flags = UfScript | UfNormalAccount | UfDontExpirePasswd | UfPasswdCantChange };
        Check(NetUserSetInfo(null, name, 1008, ref flags, out _), $"setting the options of the local account \"{name}\"");
        var comment = new UserInfo1007 { Comment = AccountComment };
        Check(NetUserSetInfo(null, name, 1007, ref comment, out _), $"setting the description of the local account \"{name}\"");
    }

    public static void DeleteLocalUser(string name)
    {
        var code = NetUserDel(null, name);
        if (code != NerrUserNotFound) Check(code, $"deleting the local account \"{name}\"");
    }

    /// <summary>The local groups the account is in.</summary>
    public static IReadOnlyList<string> GetLocalGroups(string user)
    {
        Check(NetUserGetLocalGroups(null, user, 0, 0, out var buffer, -1, out var read, out _), $"listing the groups of \"{user}\"");
        try
        {
            var groups = new List<string>();
            for (var i = 0; i < read; i++)
            {
                var entry = Marshal.PtrToStructure<LocalGroupUsersInfo0>(buffer + (i * Marshal.SizeOf<LocalGroupUsersInfo0>()));
                if (Marshal.PtrToStringUni(entry.Name) is { Length: > 0 } group) groups.Add(group);
            }

            return groups;
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    public static void RemoveFromLocalGroup(string group, string user)
    {
        var member = new LocalGroupMembersInfo3 { DomainAndName = $"{Environment.MachineName}\\{user}" };
        var code = NetLocalGroupDelMembers(null, group, 3, ref member, 1);
        if (code is not (ErrorMemberNotInAlias or ErrorNoSuchAlias)) Check(code, $"removing \"{user}\" from the group \"{group}\"");
    }

    // ---- Sign-in rights ----

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    private const uint PolicyCreateAccount = 0x10;
    private const uint PolicyLookupNames = 0x800;
    private const uint StatusObjectNameNotFound = 0xC0000034;

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes attributes, uint access, out IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, LsaUnicodeString[] rights, uint count);

    [DllImport("advapi32.dll")]
    private static extern uint LsaRemoveAccountRights(IntPtr policy, byte[] sid, [MarshalAs(UnmanagedType.U1)] bool allRights, IntPtr rights, uint count);

    [DllImport("advapi32.dll")]
    private static extern uint LsaEnumerateAccountRights(IntPtr policy, byte[] sid, out IntPtr rights, out uint count);

    [DllImport("advapi32.dll")]
    private static extern uint LsaFreeMemory(IntPtr buffer);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);

    /// <summary>Adds rights such as SeDenyInteractiveLogonRight to an account. Adding one it already has is not an error.</summary>
    public static void AddAccountRights(SecurityIdentifier account, params string[] rights)
    {
        var policy = OpenPolicy();
        var strings = new LsaUnicodeString[rights.Length];
        try
        {
            for (var i = 0; i < rights.Length; i++)
            {
                strings[i] = new LsaUnicodeString
                {
                    Buffer = Marshal.StringToHGlobalUni(rights[i]),
                    Length = (ushort)(rights[i].Length * 2),
                    MaximumLength = (ushort)((rights[i].Length + 1) * 2),
                };
            }

            CheckStatus(LsaAddAccountRights(policy, SidBytes(account), strings, (uint)strings.Length), $"denying sign-in rights ({string.Join(", ", rights)}) to {account}");
        }
        finally
        {
            foreach (var s in strings) Marshal.FreeHGlobal(s.Buffer);
            _ = LsaClose(policy);
        }
    }

    /// <summary>The rights held by an account directly, such as SeDenyInteractiveLogonRight.</summary>
    public static IReadOnlyList<string> GetAccountRights(SecurityIdentifier account)
    {
        var policy = OpenPolicy();
        try
        {
            var status = LsaEnumerateAccountRights(policy, SidBytes(account), out var buffer, out var count);
            if (status == StatusObjectNameNotFound) return [];
            CheckStatus(status, $"reading the sign-in rights of {account}");
            try
            {
                var rights = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<LsaUnicodeString>(buffer + (i * Marshal.SizeOf<LsaUnicodeString>()));
                    rights.Add(Marshal.PtrToStringUni(entry.Buffer, entry.Length / 2));
                }

                return rights;
            }
            finally
            {
                _ = LsaFreeMemory(buffer);
            }
        }
        finally
        {
            _ = LsaClose(policy);
        }
    }

    /// <summary>Takes every right away from an account that is about to be deleted, so nothing is left behind in the security policy.</summary>
    public static void RemoveAllAccountRights(SecurityIdentifier account)
    {
        var policy = OpenPolicy();
        try
        {
            var status = LsaRemoveAccountRights(policy, SidBytes(account), true, IntPtr.Zero, 0);
            if (status != StatusObjectNameNotFound) CheckStatus(status, $"removing the sign-in rights of {account}");
        }
        finally
        {
            _ = LsaClose(policy);
        }
    }

    private static IntPtr OpenPolicy()
    {
        var attributes = new LsaObjectAttributes { Length = Marshal.SizeOf<LsaObjectAttributes>() };
        CheckStatus(LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyCreateAccount | PolicyLookupNames, out var policy), "opening the local security policy");
        return policy;
    }

    private static byte[] SidBytes(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    // ---- Shares ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo502
    {
        public string NetName;
        public uint Type;
        public string? Remark;
        public uint Permissions;
        public uint MaxUses;
        public uint CurrentUses;
        public string Path;
        public string? Password;
        public uint Reserved;
        public IntPtr SecurityDescriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShareInfo2
    {
        public IntPtr NetName;
        public uint Type;
        public IntPtr Remark;
        public uint Permissions;
        public uint MaxUses;
        public uint CurrentUses;
        public IntPtr Path;
        public IntPtr Password;
    }

    /// <summary>Read, write and delete through the share: what Windows calls Change.</summary>
    private const int ShareChangeAccess = 0x001301BF;

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareAdd(string? server, int level, ref ShareInfo502 info, out int parameterError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareDel(string? server, string name, int reserved);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareGetInfo(string? server, string name, int level, out IntPtr buffer);

    /// <summary>The folder a share points at, or null when there is no share of that name.</summary>
    public static string? FindSharePath(string name)
    {
        var code = NetShareGetInfo(null, name, 2, out var buffer);
        if (code == NerrNetNameNotFound) return null;
        Check(code, $"reading the share \"{name}\"");
        try
        {
            return Marshal.PtrToStringUni(Marshal.PtrToStructure<ShareInfo2>(buffer).Path) ?? "";
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    /// <summary>Removes a share. Returns false when there was none.</summary>
    public static bool DeleteShare(string name)
    {
        var code = NetShareDel(null, name, 0);
        if (code == NerrNetNameNotFound) return false;
        Check(code, $"removing the share \"{name}\"");
        return true;
    }

    /// <summary>Shares a folder so that one account, and nobody else, has Change access through the share.</summary>
    public static void CreateShare(string name, string folder, string remark, SecurityIdentifier onlyAccount)
    {
        var acl = new RawAcl(GenericAcl.AclRevision, 1);
        acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, ShareChangeAccess, onlyAccount, false, null));
        var descriptor = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, null, null, null, acl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);

        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var info = new ShareInfo502
            {
                NetName = name,
                Type = 0,
                Remark = remark,
                MaxUses = uint.MaxValue,
                Path = folder,
                SecurityDescriptor = memory,
            };
            var code = NetShareAdd(null, 502, ref info, out var parameterError);
            Check(code, $"sharing \"{folder}\" as \"{name}\"" + (code == 87 ? $" (Windows rejected field {parameterError} of the request)" : ""));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    // ---- Console ----

    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int which, IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    /// <summary>
    /// A windowed program starts with no console. In quiet mode this joins the one it was
    /// started from, so its output shows up in a terminal. Output that was redirected to a
    /// file or pipe, as CI does, already has somewhere to go and is left alone.
    /// </summary>
    public static void AttachParentConsole()
    {
        var hasOut = IsUsable(GetStdHandle(StdOutputHandle));
        var hasError = IsUsable(GetStdHandle(StdErrorHandle));
        if (hasOut && hasError) return;
        if (!AttachConsole(AttachParentProcess)) return;

        const uint genericWrite = 0x40000000;
        const uint shareReadWrite = 0x3;
        const uint openExisting = 3;
        var console = CreateFileW("CONOUT$", genericWrite, shareReadWrite, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        if (!IsUsable(console)) return;
        if (!hasOut) SetStdHandle(StdOutputHandle, console);
        if (!hasError) SetStdHandle(StdErrorHandle, console);
    }

    private static bool IsUsable(IntPtr handle) => handle != IntPtr.Zero && handle != InvalidHandle;

    // ---- Errors ----

    private static void Check(int code, string doing)
    {
        if (code == NerrSuccess) return;
        var reason = code switch
        {
            NerrUserExists => "an account with that name already exists",
            NerrUserNotFound => "there is no such account",
            NerrPasswordTooShort => "the password does not meet this server's password policy",
            NerrNetNameNotFound => "there is no such share",
            2118 => "a share with that name already exists",
            2114 => "the Server service is not running, so nothing can be shared",
            2202 => "the name is not a valid account name",
            2223 => "there is already a group with that name",
            _ => new Win32Exception(code).Message,
        };
        throw new SetupException($"Windows refused {doing}: {reason} (error {code}).");
    }

    private static void CheckStatus(uint status, string doing)
    {
        if (status == 0) return;
        var code = LsaNtStatusToWinError(status);
        throw new SetupException($"Windows refused {doing}: {new Win32Exception(code).Message} (error {code}, status 0x{status:X8}).");
    }
}
