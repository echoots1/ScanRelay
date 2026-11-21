namespace ScanRelay.Installer.Tests;

public class RulesTests
{
    // The values Windows gives these rights (FileSystemRights), written out so a change to our own enum shows up here.
    private const int ReadDataOrList = 0x1;
    private const int CreateFilesOrWriteData = 0x2;
    private const int AppendDataOrCreateFolders = 0x4;
    private const int WriteExtendedAttributes = 0x10;
    private const int Traverse = 0x20;
    private const int DeleteSubfoldersAndFiles = 0x40;
    private const int ReadAttributes = 0x80;
    private const int WriteAttributes = 0x100;
    private const int Delete = 0x10000;
    private const int Synchronize = 0x100000;

    private const int StrictFiles = CreateFilesOrWriteData | AppendDataOrCreateFolders | WriteAttributes | WriteExtendedAttributes | ReadAttributes | Synchronize;
    private const int StrictFolders = StrictFiles | Traverse;

    [Fact]
    public void With_no_modules_the_printer_account_can_only_drop_files()
    {
        var rights = InboxRights.For([]);

        Assert.Equal(StrictFolders, (int)rights.Folders);
        Assert.Equal(StrictFiles, (int)rights.Files);
        Assert.Equal(0, (int)rights.Folders & (ReadDataOrList | Delete | DeleteSubfoldersAndFiles));
        Assert.Equal(0, (int)rights.Files & (ReadDataOrList | Delete));
        Assert.Equal(InboxRights.Strict, rights);
        Assert.Equal("create and write only", rights.Describe());
    }

    [Fact]
    public void Modules_that_ask_for_the_default_change_nothing()
    {
        Assert.Equal(InboxRights.Strict, InboxRights.For([["create", "write"], ["write", "create"]]));
    }

    [Fact]
    public void Read_is_given_on_files_only()
    {
        var rights = InboxRights.For([["create", "write", "read"]]);
        Assert.Equal(StrictFiles | ReadDataOrList, (int)rights.Files);
        Assert.Equal(StrictFolders, (int)rights.Folders);
    }

    [Fact]
    public void List_is_given_on_folders_only()
    {
        var rights = InboxRights.For([["create", "write", "list"]]);
        Assert.Equal(StrictFolders | ReadDataOrList, (int)rights.Folders);
        Assert.Equal(StrictFiles, (int)rights.Files);
    }

    [Fact]
    public void Delete_covers_files_and_what_is_inside_folders()
    {
        var rights = InboxRights.For([["create", "write", "delete"]]);
        Assert.Equal(StrictFolders | Delete | DeleteSubfoldersAndFiles, (int)rights.Folders);
        Assert.Equal(StrictFiles | Delete, (int)rights.Files);
    }

    [Fact]
    public void The_widest_need_of_any_installed_module_wins()
    {
        var rights = InboxRights.For([["create", "write"], ["create", "write", "list"], ["create", "write", "read", "delete"]]);
        Assert.Equal(StrictFolders | ReadDataOrList | Delete | DeleteSubfoldersAndFiles, (int)rights.Folders);
        Assert.Equal(StrictFiles | ReadDataOrList | Delete, (int)rights.Files);
        Assert.Contains("because an installed module needs it", rights.Describe());
    }

    [Fact]
    public void Service_sid_is_worked_out_the_way_windows_does()
    {
        // TrustedInstaller's SID is the same on every Windows machine, which makes it a fixed point to check against.
        Assert.Equal("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", ServiceAccount.Sid("TrustedInstaller"));
        Assert.Equal(ServiceAccount.Sid("scanrelay"), ServiceAccount.Sid("ScanRelay"));
        Assert.StartsWith("S-1-5-80-", ServiceAccount.Sid());
        Assert.Equal(@"NT SERVICE\ScanRelay", ServiceAccount.AccountName);
    }

    [Fact]
    public void Backup_is_named_for_the_old_version_and_the_time()
    {
        var when = new DateTime(2026, 3, 9, 14, 5, 7, DateTimeKind.Utc);
        Assert.Equal("scanrelay-0.1.0-20260309140507.db", DatabaseBackup.FileName("0.1.0", when));
        Assert.Equal("scanrelay-unknown-20260309140507.db", DatabaseBackup.FileName(null, when));
        Assert.Equal("scanrelay-unknown-20260309140507.db", DatabaseBackup.FileName("", when));
        Assert.Equal("scanrelay-1.2.3-beta-20260309140507.db", DatabaseBackup.FileName("1.2.3-beta", when));
        Assert.Equal(
            @"D:\Data\ScanRelay\Backups\scanrelay-0.9.0-20260309140507.db",
            SetupEngine.BackupPath(@"D:\Data\ScanRelay\", "0.9.0", when));
        Assert.Equal(["", "-wal", "-shm"], DatabaseBackup.Suffixes);
    }

    [Fact]
    public void Backup_name_cannot_carry_a_path()
    {
        var name = DatabaseBackup.FileName(@"..\..\evil/1.0", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain('/', name);
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1.0", "0.2.0", true)]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData(null, "0.1.0", true)]
    [InlineData("", "0.1.0", true)]
    public void Backup_is_taken_whenever_the_version_changes(string? installed, string now, bool expected)
    {
        Assert.Equal(expected, DatabaseBackup.IsNeeded(installed, now));
    }

    [Fact]
    public void Generated_passwords_are_long_mixed_and_different()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => PrinterPassword.Generate()).ToList();
        Assert.All(passwords, p =>
        {
            Assert.Equal(32, p.Length);
            Assert.Contains(p, char.IsAsciiLetterUpper);
            Assert.Contains(p, char.IsAsciiLetterLower);
            Assert.Contains(p, char.IsAsciiDigit);
            Assert.All(p, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        });
        Assert.Equal(50, passwords.Distinct().Count());
    }

    [Theory]
    [InlineData(@"C:\Data", @"c:\data\", true)]
    [InlineData(@"C:\Data\Inbox", @"C:\Data", true)]
    [InlineData(@"C:\DataMore", @"C:\Data", false)]
    [InlineData(@"C:\Data", @"C:\Data\Inbox", false)]
    public void Inside_is_judged_by_whole_folder_names(string path, string folder, bool expected)
    {
        Assert.Equal(expected, WinPath.IsSameOrInside(path, folder));
    }
}
