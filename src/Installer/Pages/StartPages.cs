namespace ScanRelay.Installer.Pages;

/// <summary>First page of a new install.</summary>
internal sealed class WelcomePage : WizardPage
{
    public WelcomePage(WizardModel model)
        : base(model)
    {
        AddText($"This installs ScanRelay {model.Engine.Version} on this server.");
        AddText("ScanRelay takes the scans that printers save to a folder on this server and puts each one into the right person's OneDrive.", spaceAbove: true);
        AddText("Setup asks a few questions: where to install, the address of the admin screen, the web certificate, the account printers use, and which printer modules to add.");
        AddText("Before anything is changed, setup shows a summary of everything it will create on this server. Nothing is done until you press Install there.", spaceAbove: true);
        AddText("When setup finishes, the first-run screen opens in your browser to connect ScanRelay to Microsoft 365.");
    }

    public override string Heading => "Welcome to ScanRelay Setup";

    public override string Explanation => "Install ScanRelay on this server.";
}

/// <summary>Shown when the same version is already installed: change it, repair it or remove it.</summary>
internal sealed class MaintenancePage : WizardPage
{
    private readonly RadioButton _modify;
    private readonly RadioButton _repair;
    private readonly RadioButton _remove;
    private readonly CheckBox _reopen;
    private readonly CheckBox _renew;

    public MaintenancePage(WizardModel model)
        : base(model)
    {
        AddText($"ScanRelay {model.State?.Version} is installed in {model.State?.Answers.InstallFolder}.");

        _modify = AddRadio("&Modify", chosen: true);
        AddNote("Add, update or remove printer modules, or change the folders, the share, the port, the web certificate or the printer account.", 8);

        _repair = AddRadio("&Repair", chosen: false);
        AddNote("Put everything back as setup made it: program files, rights, the account, the share, certificates and the service. Settings and scans are kept.");
        _reopen = AddCheck("Reopen the local &setup screen (use this when nobody can sign in to the admin screen)", ticked: false, indent: 19);
        _renew = AddCheck("Renew the &Graph certificate (it must then be uploaded again in the admin screen)", ticked: false, indent: 19);
        _renew.Margin = new Padding(22, 3, 3, 8);

        _remove = AddRadio("&Uninstall", chosen: false);
        AddNote("Remove ScanRelay from this server. You are asked whether to keep the data folder.");

        _repair.CheckedChanged += (_, _) => EnableOptions();
        EnableOptions();
    }

    public override string Heading => "Change, repair or remove ScanRelay";

    public override string Explanation => "ScanRelay is already installed on this server. Choose what to do.";

    public override bool Leaving()
    {
        Model.Flow = _modify.Checked ? WizardFlow.Modify : _repair.Checked ? WizardFlow.Repair : WizardFlow.Uninstall;
        if (Model.Flow == WizardFlow.Repair) Model.UseInstalledAnswers();
        Model.Answers.ReopenSetup = _repair.Checked && _reopen.Checked;
        Model.Answers.RenewGraphCertificate = _repair.Checked && _renew.Checked;
        return true;
    }

    private void EnableOptions()
    {
        _reopen.Enabled = _repair.Checked;
        _renew.Enabled = _repair.Checked;
    }
}

/// <summary>Shown when this setup is a different version from what is installed. Warns before anything is replaced.</summary>
internal sealed class UpdatePage : WizardPage
{
    private readonly TextBox _warnings;
    private bool _checked;
    private bool _checking;

    public UpdatePage(WizardModel model)
        : base(model)
    {
        var installed = model.State?.Version is { Length: > 0 } v ? v : "an unknown version";
        AddText($"ScanRelay {installed} is installed in {model.State?.Answers.InstallFolder}. This setup replaces it with version {model.Engine.Version}.");
        if (Version.TryParse(model.State?.Version, out var old) && old > Version.Parse(model.Engine.Version))
        {
            AddText("The installed version is NEWER than this setup. Continuing puts the older version back. Only do this to undo an update, and expect to restore the database backup that update made.");
        }

        AddText("Setup stops the service, backs up the database, replaces the program files, brings the installed modules to matching versions and starts the service again. Scans are not lost: printers keep saving to the share and waiting files are processed afterwards.");
        AddCaption("&Before you continue:");
        _warnings = AddReadOnlyBox("Warnings about this update");
    }

    public override string Heading => "Update ScanRelay";

    public override string Explanation => "A different version of ScanRelay is installed on this server.";

    public override bool CanGoNext => _checked;


    public override async void Entering()
    {
        if (_checked || _checking || Model.State is null) return;
        _checking = true;
        _warnings.Text = "Checking the installed modules against the project's list on GitHub...";
        IReadOnlyList<string> warnings;
        try
        {
            warnings = await Task.Run(() => Model.Engine.CheckModulesAsync(Model.Answers, Model.State));
        }
        catch (Exception e)
        {
            warnings = ["The installed modules could not be checked: " + e.Message];
        }

        if (IsDisposed) return;
        _warnings.Text = warnings.Count == 0
            ? "Nothing needs your attention. Every installed module has a version for this release."
            : string.Join(Environment.NewLine + Environment.NewLine, warnings) + Environment.NewLine + Environment.NewLine + "You can continue. The warnings above say what will not work until it is put right.";
        _checking = false;
        _checked = true;
        OnStateChanged();
    }
}

/// <summary>Asks the one question uninstall has: keep the data folder or not.</summary>
internal sealed class UninstallPage : WizardPage
{
    private readonly CheckBox _keep;

    public UninstallPage(WizardModel model)
        : base(model)
    {
        var answers = model.Answers;
        AddText("Setup will remove:");
        AddText(
            $"•  the ScanRelay service and the program files in {answers.InstallFolder}\n"
            + $"•  the hidden share {answers.ShareName} and the local account {answers.PrinterAccount}\n"
            + $"•  the firewall rule \"{SetupAnswers.FirewallRuleName}\"\n"
            + "•  the certificates setup created (a web certificate you chose yourself is left alone)\n"
            + "•  ScanRelay's entry in Windows installed apps");
        AddText($"The data folder, {answers.DataFolder}, holds scans that have not been uploaded yet, the database with all settings and history, and the logs.", spaceAbove: true);
        _keep = AddCheck("&Keep the data folder", model.KeepData);
        AddText("Keep it if you plan to install ScanRelay again or want the records. Untick it to delete everything. Nothing in anyone's OneDrive is touched either way.");
        AddText("Printers will still have ScanRelay's entries in their address books. Remove those at the printers, or before uninstalling, from the admin screen.", spaceAbove: true);
    }

    public override string Heading => "Remove ScanRelay";

    public override string Explanation => "Uninstall ScanRelay from this server.";

    public override string NextCaption => "&Remove";

    public override bool Leaving()
    {
        Model.Flow = WizardFlow.Uninstall;
        Model.KeepData = _keep.Checked;
        if (_keep.Checked) return true;
        var answer = MessageBox.Show(
            this,
            $"The data folder {Model.Answers.DataFolder} will be deleted, with any scans still waiting in it, the database and the logs. This cannot be undone.\n\nDelete it?",
            "ScanRelay Setup",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return answer == DialogResult.Yes;
    }
}
