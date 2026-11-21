namespace ScanRelay.Installer;

internal enum WizardFlow
{
    Install,

    /// <summary>Already installed, same version: the admin chooses Modify, Repair or Uninstall.</summary>
    Maintenance,
    Modify,
    Repair,
    Update,
    Uninstall,
}

/// <summary>What the pages share: the answers so far and what is already installed.</summary>
internal sealed class WizardModel(SetupEngine engine, InstalledState? state, WizardFlow flow)
{
    public SetupEngine Engine => engine;

    /// <summary>The existing install, or null for a new one.</summary>
    public InstalledState? State => state;

    public SetupAnswers Answers { get; private set; } = state?.Answers.Clone() ?? new SetupAnswers();

    /// <summary>Goes back to what is installed, dropping anything typed on the question pages. Used by Repair.</summary>
    public void UseInstalledAnswers() => Answers = state?.Answers.Clone() ?? new SetupAnswers();

    public WizardFlow Flow { get; set; } = flow;

    /// <summary>Uninstall: leave the data folder in place. Keeping is the safe default.</summary>
    public bool KeepData { get; set; } = true;

    public SetupResult? Result { get; set; }

    public bool IsUninstall => Flow == WizardFlow.Uninstall;
}

/// <summary>
/// One page of the wizard. Controls are stacked top to bottom in the order they are
/// added, which is also their tab order, so every page reads and tabs the same way.
/// </summary>
internal abstract class WizardPage : UserControl
{
    private readonly TableLayoutPanel _rows;
    private int _nextTabIndex;

    protected WizardPage(WizardModel model)
    {
        Model = model;
        Dock = DockStyle.Fill;
        Visible = false;
        _rows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(0, 0, 4, 0),
        };
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_rows);
    }

    protected WizardModel Model { get; }

    public abstract string Heading { get; }

    public abstract string Explanation { get; }

    /// <summary>The words on the Next button while this page shows.</summary>
    public virtual string NextCaption => "&Next >";

    public virtual bool CanGoNext => true;

    public virtual bool CanGoBack => true;

    /// <summary>True while work is under way that closing the window would interrupt.</summary>
    public virtual bool IsWorking => false;

    /// <summary>The last page: Next becomes Finish and there is nothing left to cancel.</summary>
    public virtual bool IsLast => false;

    /// <summary>Raised when what the buttons should allow has changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when the page has done its work and the wizard should move on by itself.</summary>
    public event EventHandler? Completed;

    /// <summary>Called each time the page is shown.</summary>
    public virtual void Entering()
    {
    }

    /// <summary>Called when Next is pressed. Stores the page's answers; returns false to stay and fix something.</summary>
    public virtual bool Leaving() => true;

    /// <summary>Called when the admin asks to stop while <see cref="IsWorking"/>.</summary>
    public virtual void CancelWork()
    {
    }

    protected void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    protected void OnCompleted() => Completed?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows the problems that belong to this page. Returns true when there are none.</summary>
    protected bool Check(params string[] about)
    {
        var problems = Model.Answers.Validate().Where(p => about.Any(a => p.Contains(a, StringComparison.OrdinalIgnoreCase))).ToList();
        if (problems.Count == 0) return true;
        MessageBox.Show(this, string.Join(Environment.NewLine + Environment.NewLine, problems), "ScanRelay Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    /// <summary>Adds a control as the next row. A row that grows takes whatever height is left over.</summary>
    protected T Add<T>(T control, bool grows = false, int indent = 0)
        where T : Control
    {
        control.TabIndex = _nextTabIndex++;
        control.Margin = new Padding(3 + indent, 3, 3, grows ? 6 : 3);
        if (grows) control.Dock = DockStyle.Fill;
        else if (control is not (ButtonBase or NumericUpDown)) control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        _rows.RowStyles.Add(grows ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        _rows.Controls.Add(control, 0, _rows.RowCount++);
        return control;
    }

    /// <summary>A paragraph that wraps to the page's width.</summary>
    protected Label AddText(string text, bool spaceAbove = false)
    {
        var label = Add(new Label { Text = text, AutoSize = true, UseMnemonic = false });
        label.Margin = new Padding(3, spaceAbove ? 12 : 3, 3, 6);
        return label;
    }

    /// <summary>A caption with an Alt-key, tied to the control added straight after it.</summary>
    protected Label AddCaption(string textWithAccelerator)
    {
        var label = Add(new Label { Text = textWithAccelerator, AutoSize = true, UseMnemonic = true });
        label.Margin = new Padding(3, 9, 3, 0);
        return label;
    }

    protected TextBox AddField(string captionWithAccelerator, string value, int maxLength = 260)
    {
        AddCaption(captionWithAccelerator);
        return Add(new TextBox { Text = value, MaxLength = maxLength, AccessibleName = captionWithAccelerator.Replace("&", "") });
    }

    /// <summary>A folder box with a Browse button beside it.</summary>
    protected TextBox AddFolderField(string captionWithAccelerator, string value, string browseCaption, string browseDescription)
    {
        AddCaption(captionWithAccelerator);
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var box = new TextBox { Text = value, MaxLength = 200, Anchor = AnchorStyles.Left | AnchorStyles.Right, TabIndex = 0, AccessibleName = captionWithAccelerator.Replace("&", "") };
        var browse = new Button { Text = browseCaption, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 1, 6, 1), TabIndex = 1 };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = browseDescription, UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (Directory.Exists(box.Text)) dialog.InitialDirectory = box.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
        };
        row.Controls.Add(box, 0, 0);
        row.Controls.Add(browse, 1, 0);
        Add(row);
        row.Margin = new Padding(0);
        return box;
    }

    protected CheckBox AddCheck(string textWithAccelerator, bool ticked, int indent = 0) =>
        Add(new CheckBox { Text = textWithAccelerator, Checked = ticked, AutoSize = true }, indent: indent);

    protected RadioButton AddRadio(string textWithAccelerator, bool chosen) =>
        Add(new RadioButton { Text = textWithAccelerator, Checked = chosen, AutoSize = true });

    protected Button AddButton(string textWithAccelerator, EventHandler click)
    {
        var button = Add(new Button { Text = textWithAccelerator, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 1, 6, 1) });
        button.Click += click;
        return button;
    }

    /// <summary>A read-only box of text that can be scrolled, selected and copied.</summary>
    protected TextBox AddReadOnlyBox(string accessibleName, bool grows = true) =>
        Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, AccessibleName = accessibleName, BackColor = SystemColors.Window }, grows);
}
