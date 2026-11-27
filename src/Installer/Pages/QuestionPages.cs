using System.Globalization;

namespace ScanRelay.Installer.Pages;

internal sealed class FoldersPage : WizardPage
{
    private readonly TextBox _install;
    private readonly TextBox _data;

    public FoldersPage(WizardModel model)
        : base(model)
    {
        _install = AddFolderField("&Install the program in:", model.Answers.InstallFolder, "B&rowse...", "Choose where to install ScanRelay");
        AddText("The service can read this folder but cannot write to it.");
        _data = AddFolderField("Keep the &data folder in:", model.Answers.DataFolder, "Br&owse...", "Choose where ScanRelay keeps its data");
        AddText("The data folder holds scans on their way to OneDrive, the database and the logs. It is hidden, and only the service and administrators can open it. Processed scans stay there for 7 days, so choose a drive with room, and consider disk encryption on it.");
        if (model.State is not null)
        {
            AddText("Changing a folder moves what is in it. The service is stopped while that happens.", spaceAbove: true);
        }
    }

    public override string Heading => "Folders";

    public override string Explanation => "Where to install the program and where to keep its data.";

    public override bool Leaving()
    {
        Model.Answers.InstallFolder = _install.Text.Trim();
        Model.Answers.DataFolder = _data.Text.Trim();
        return Check("install folder", "data folder");
    }
}

internal sealed class AdminScreenPage : WizardPage
{
    private readonly TextBox _host;
    private readonly NumericUpDown _port;
    private readonly CheckBox _firewall;
    private readonly Label _address;

    public AdminScreenPage(WizardModel model)
        : base(model)
    {
        AddText("The admin screen is a web page served by this server over HTTPS. Admins open it in a browser.");
        _host = AddField("&Host name admins will type:", model.Answers.HostName, maxLength: 253);
        AddCaption("&Port:");
        _port = Add(new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Math.Clamp(model.Answers.Port, 1, 65535), Width = 100, AccessibleName = "Port" });
        _address = AddText("", spaceAbove: true);
        _firewall = AddCheck("Add a Windows &Firewall rule so admins can reach this port from other computers", model.Answers.AddFirewallRule);
        AddText("Without the rule, the admin screen can only be opened on this server itself. Until sign-in is set up in the first-run screen, it only answers on this server in any case.");
        AddText("The same host name is what printers use to find the share, so it must be a name they can look up.", spaceAbove: true);

        _host.TextChanged += (_, _) => ShowAddress();
        _port.ValueChanged += (_, _) => ShowAddress();
        ShowAddress();
    }

    public override string Heading => "Admin screen";

    public override string Explanation => "The address and port of the ScanRelay admin screen.";

    public override bool Leaving()
    {
        Model.Answers.HostName = _host.Text.Trim();
        Model.Answers.Port = (int)_port.Value;
        Model.Answers.AddFirewallRule = _firewall.Checked;
        return Check("host name", "port must");
    }

    private void ShowAddress() =>
        _address.Text = $"The admin screen will be at:  https://{_host.Text.Trim()}:{((int)_port.Value).ToString(CultureInfo.InvariantCulture)}";
}

internal sealed class WebCertificatePage : WizardPage
{
    private readonly RadioButton _selfSigned;
    private readonly RadioButton _existing;
    private readonly ListView _list;

    public WebCertificatePage(WizardModel model)
        : base(model)
    {
        AddText("The admin screen needs a certificate for HTTPS.");
        _selfSigned = AddRadio("Create a &self-signed certificate", model.Answers.WebCertThumbprint is null);
        AddNote("Works straight away. Browsers show a warning until the certificate is trusted on the admins' computers or replaced with one from your certificate authority.", 8);
        _existing = AddRadio("&Use a certificate already on this server", model.Answers.WebCertThumbprint is not null);
        AddNote("Certificates in the Local Computer, Personal store that have a private key:");
        _list = Add(
            new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                AccessibleName = "Certificates on this server",
            },
            grows: true,
            indent: 19);
        _list.Columns.Add("Issued to", 220);
        _list.Columns.Add("Expires", 100);
        _list.Columns.Add("Thumbprint", 300);

        _existing.CheckedChanged += (_, _) => _list.Enabled = _existing.Checked;
        _list.Enabled = _existing.Checked;
    }

    public override string Heading => "Web certificate";

    public override string Explanation => "The certificate that secures the admin screen.";

    public override void Entering()
    {
        var wanted = _list.SelectedItems.Count > 0 ? (string)_list.SelectedItems[0].Tag! : Model.Answers.WebCertThumbprint;
        _list.BeginUpdate();
        _list.Items.Clear();
        try
        {
            foreach (var choice in SetupEngine.ListWebCertificates())
            {
                var item = new ListViewItem([choice.Subject, choice.Expires.ToString("d", CultureInfo.CurrentCulture) + (choice.Expires < DateTime.Now ? " (expired)" : ""), choice.Thumbprint]) { Tag = choice.Thumbprint };
                _list.Items.Add(item);
                if (wanted is not null && SetupAnswers.NormaliseThumbprint(wanted) == choice.Thumbprint) item.Selected = true;
            }
        }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            _list.Items.Add(new ListViewItem(["The certificate store could not be read: " + e.Message, "", ""]));
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    public override bool Leaving()
    {
        if (_selfSigned.Checked)
        {
            Model.Answers.WebCertThumbprint = null;
            return true;
        }

        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not string thumbprint)
        {
            MessageBox.Show(this, "Select a certificate in the list, or choose a self-signed certificate.", "ScanRelay Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        Model.Answers.WebCertThumbprint = thumbprint;
        return Check("thumbprint");
    }
}

internal sealed class PrinterAccountPage : WizardPage
{
    private readonly TextBox _account;
    private readonly TextBox _share;
    private readonly Label _path;

    public PrinterAccountPage(WizardModel model)
        : base(model)
    {
        AddText("Printers save scans to a hidden share on this server, signing in with a local account that setup creates for that and nothing else.");
        _account = AddField("&Account name for printers:", model.Answers.PrinterAccount, maxLength: 20);
        AddText("Setup generates a long random password for the account and stores it encrypted. You never need to see or type it: ScanRelay puts it into the printers' address books itself. The account belongs to no groups and cannot sign in at the server or through Remote Desktop.");
        _share = AddField("&Share name (must end with $ so it is hidden):", model.Answers.ShareName, maxLength: 80);
        _path = AddText("");
        AddText("Only this account can use the share, and only to create and write files. It cannot list, read or delete what is there unless a printer module you install needs that.");
        AddText("Setup does not limit which computers can reach the share. Limiting it to the printer network is a firewall task for IT.", spaceAbove: true);
        if (model.State is not null)
        {
            AddText("Changing the account name creates a new account with a new password and deletes the old one. Printers get the new details with the next address book push.", spaceAbove: true);
        }

        _share.TextChanged += (_, _) => ShowPath();
        ShowPath();
    }

    public override string Heading => "Printer account";

    public override string Explanation => "The account and hidden share that printers save scans with.";

    public override bool Leaving()
    {
        Model.Answers.PrinterAccount = _account.Text.Trim();
        Model.Answers.ShareName = _share.Text.Trim();
        return Check("printer account name", "share name");
    }

    private void ShowPath() => _path.Text = $"Printers will save to:  \\\\{Environment.MachineName}\\{_share.Text.Trim()}";
}

internal sealed class UpdatesPage : WizardPage
{
    private readonly CheckBox _check;

    public UpdatesPage(WizardModel model)
        : base(model)
    {
        AddText("ScanRelay can look on GitHub once a day for a new release. When there is one, it shows a banner on the Dashboard and emails the admins what changed and what to do. It never updates itself: updating always means running the new setup here.");
        _check = AddCheck("ScanRelay may &check GitHub for updates", model.Answers.UpdateCheck);
        AddText("Nothing about your organisation is sent: the check only downloads the public release list. If you turn this off, watch the project's releases page yourself. It can be changed later in Settings.");
    }

    public override string Heading => "Updates";

    public override string Explanation => "Whether ScanRelay may check for new releases.";

    public override bool Leaving()
    {
        Model.Answers.UpdateCheck = _check.Checked;
        return true;
    }
}

internal sealed class ShortcutsPage : WizardPage
{
    private readonly CheckBox _desktop;
    private readonly CheckBox _tray;

    public ShortcutsPage(WizardModel model)
        : base(model)
    {
        AddText("ScanRelay runs as a Windows service with no window of its own. These two make it easy to reach on this server.");
        _desktop = AddCheck("Put a ScanRelay icon on the &desktop", model.Answers.DesktopShortcut);
        AddText("For everyone who signs in to this server. It opens the admin screen in the browser. If the service is stopped, it starts it first; Windows asks for an administrator to do that.");
        _tray = AddCheck("Show a ScanRelay icon by the &clock", model.Answers.TrayIcon, 0);
        AddText("From each person's next sign-in. A green dot means running, red means stopped. Its menu opens the admin screen and starts, stops or restarts the service; Windows asks for an administrator for those three.");
    }

    public override string Heading => "Icons";

    public override string Explanation => "How ScanRelay is reached on this server.";

    public override bool Leaving()
    {
        Model.Answers.DesktopShortcut = _desktop.Checked;
        Model.Answers.TrayIcon = _tray.Checked;
        return true;
    }
}
