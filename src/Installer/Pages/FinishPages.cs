namespace ScanRelay.Installer.Pages;

/// <summary>Everything setup is about to do, for the admin to read before agreeing.</summary>
internal sealed class SummaryPage : WizardPage
{
    private readonly TextBox _text;
    private bool _valid = true;

    public SummaryPage(WizardModel model)
        : base(model)
    {
        AddCaption("&What setup will do:");
        _text = AddReadOnlyBox("What setup will do");
    }

    public override string Heading => "Summary";

    public override string Explanation => "Read what setup will create on this server. Nothing is done until you confirm.";

    public override string NextCaption => Model.Flow switch
    {
        WizardFlow.Repair => "&Repair",
        WizardFlow.Update => "&Update",
        WizardFlow.Modify => "&Apply",
        _ => "&Install",
    };

    public override bool CanGoNext => _valid;

    public override void Entering()
    {
        var problems = Model.Answers.Validate();
        _valid = problems.Count == 0;
        var summary = SetupEngine.Describe(Model.Answers, Model.State, Model.Engine.Version);
        _text.Text = (_valid ? summary : "Go back and put these right first:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  " + p)))
            .ReplaceLineEndings(Environment.NewLine);
        _text.Select(0, 0);
        OnStateChanged();
    }
}

/// <summary>Runs the engine on a background task and shows each step and log line as it happens.</summary>
internal sealed class ProgressPage : WizardPage
{
    private readonly ListBox _steps;
    private readonly ProgressBar _bar;
    private readonly TextBox _log;
    private readonly Label _status;
    private CancellationTokenSource? _stop;
    private IReadOnlyList<string> _names = [];
    private bool _running;
    private bool _failed;
    private int _current;

    public ProgressPage(WizardModel model)
        : base(model)
    {
        _status = AddText("");
        _bar = Add(new ProgressBar { Minimum = 0, Height = 18 });
        AddCaption("&Steps:");
        _steps = Add(new ListBox { IntegralHeight = false, SelectionMode = SelectionMode.One, AccessibleName = "Steps" }, grows: true);
        AddCaption("&Details:");
        _log = AddReadOnlyBox("Details");
    }

    public override string Heading => Model.IsUninstall ? "Removing ScanRelay" : "Setting up ScanRelay";

    public override string Explanation => "This takes a minute or two. Everything shown here is also written to " + SetupEnvironment.DefaultLogPath + ".";

    public override bool CanGoNext => false;

    public override bool CanGoBack => !_running && _failed && !Model.IsUninstall;

    public override bool IsWorking => _running;

    public override void Entering()
    {
        if (!_running && Model.Result is null) Start();
    }

    public override void CancelWork()
    {
        _stop?.Cancel();
        _status.Text = "Stopping after the current step...";
    }

    private async void Start()
    {
        _running = true;
        _failed = false;
        _current = 0;
        _names = Model.IsUninstall ? SetupEngine.UninstallSteps : SetupEngine.InstallSteps;
        _steps.Items.Clear();
        foreach (var name in _names) _steps.Items.Add("      " + name);
        _bar.Maximum = _names.Count;
        _bar.Value = 0;
        _log.Clear();
        _status.Text = "Working...";
        _stop = new CancellationTokenSource();
        OnStateChanged();

        // Created here, on the window's thread, so each report arrives back on it.
        var progress = new Progress<SetupProgress>(Report);
        try
        {
            var token = _stop.Token;
            Model.Result = Model.IsUninstall
                ? await Model.Engine.UninstallAsync(Model.KeepData, progress, token)
                : await Task.Run(() => Model.Engine.ApplyAsync(Model.Answers, progress, token));
            Mark(_current, "✓");
            _bar.Value = _bar.Maximum;
            _status.Text = "Done.";
            _running = false;
            OnStateChanged();
            OnCompleted();
        }
        catch (OperationCanceledException)
        {
            Finish("!", "Setup was stopped before it finished. Run it again to complete or remove the installation.");
        }
        catch (Exception e)
        {
            _log.AppendText(Environment.NewLine + (e is SetupException ? e.Message : e.ToString()).ReplaceLineEndings(Environment.NewLine) + Environment.NewLine);
            Finish("✗", "Setup could not finish. The reason is in the details below" + (Model.IsUninstall ? "." : ". Go back to try again, or close setup and fix the cause first; running setup again carries on from here."));
        }
    }

    private void Finish(string mark, string status)
    {
        Mark(_current, mark);
        _status.Text = status;
        _running = false;
        _failed = true;
        OnStateChanged();
    }

    private void Report(SetupProgress progress)
    {
        if (IsDisposed) return;
        if (progress.StepName is not null && progress.Step >= 1)
        {
            Mark(_current, "✓");
            _current = progress.Step;
            Mark(_current, "▶");
            _bar.Value = Math.Clamp(progress.Step - 1, 0, _bar.Maximum);
            _status.Text = $"Step {progress.Step} of {progress.Steps}: {progress.StepName}";
        }

        _log.AppendText(progress.Line.ReplaceLineEndings(Environment.NewLine) + Environment.NewLine);
    }

    /// <summary>Puts a mark in front of a step, counting from 1.</summary>
    private void Mark(int step, string mark)
    {
        if (step < 1 || step > _names.Count) return;
        _steps.Items[step - 1] = $"{mark}   {_names[step - 1]}";
        _steps.TopIndex = Math.Max(0, step - 3);
    }
}

/// <summary>The last page: what happened, anything to know, and where to go next.</summary>
internal sealed class DonePage : WizardPage
{
    private readonly Label _message;
    private readonly TextBox _warnings;
    private readonly Label _warningsCaption;
    private readonly CheckBox _open;

    public DonePage(WizardModel model)
        : base(model)
    {
        _message = AddText("");
        _open = AddCheck("&Open the ScanRelay admin screen now", ticked: true);
        _warningsCaption = AddCaption("&Please read:");
        _warnings = AddReadOnlyBox("Warnings");
    }

    public override string Heading => Model.IsUninstall ? "ScanRelay has been removed" : "ScanRelay is ready";

    public override string Explanation => Model.IsUninstall ? "Setup has finished removing ScanRelay." : "Setup has finished and the service is running.";

    public override bool IsLast => true;

    public override bool CanGoBack => false;

    public override void Entering()
    {
        var answers = Model.Answers;
        var warnings = Model.Result?.Warnings ?? [];
        _open.Visible = !Model.IsUninstall;
        _warnings.Visible = warnings.Count > 0;
        _warningsCaption.Visible = warnings.Count > 0;
        _warnings.Text = string.Join(Environment.NewLine + Environment.NewLine, warnings);

        _message.Text = Model.Flow switch
        {
            WizardFlow.Uninstall => "The service, the share, the printer account, the firewall rule and the program files have been removed.\n\n"
                + (Model.KeepData
                    ? $"The data folder was kept: {answers.DataFolder}. Only administrators can open it. Delete it yourself when you no longer need it."
                    : "The data folder was deleted.")
                + (WinPath.IsSameOrInside(Environment.ProcessPath ?? "", answers.InstallFolder) ? "\n\nThe program folder itself disappears a few seconds after you close this window." : ""),
            WizardFlow.Install => $"ScanRelay {Model.Engine.Version} is installed and its service is running.\n\n"
                + $"Next, the first-run screen connects ScanRelay to Microsoft 365. It opens on this server at {answers.LocalUrl}. "
                + "Until that is finished the admin screen answers only on this server, and without sign-in.\n\n"
                + $"Afterwards admins reach it at {answers.Url}.",
            WizardFlow.Update => $"ScanRelay has been updated to {Model.Engine.Version} and its service is running. The database was backed up to {answers.DataFolder}\\Backups before the update.\n\n"
                + "Open the admin screen to see the health check, and any steps this release asks for.",
            _ => $"The changes have been made and the service is running. The admin screen is at {answers.Url}."
                + (answers.ReopenSetup ? $"\n\nThe local setup screen is open again: on this server, go to {answers.LocalUrl} to put sign-in right." : ""),
        };
    }

    public override bool Leaving()
    {
        if (!Model.IsUninstall && _open.Checked) WizardForm.OpenInBrowser(Model.Answers.LocalUrl + "/");
        return true;
    }
}
