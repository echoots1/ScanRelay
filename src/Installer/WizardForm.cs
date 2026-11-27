using System.Diagnostics;
using ScanRelay.Installer.Pages;

namespace ScanRelay.Installer;

/// <summary>The one setup window: a heading, the current page, and Back, Next and Cancel.</summary>
internal sealed class WizardForm : Form
{
    private const string Title = "ScanRelay Setup";

    private readonly WizardModel _model;
    private readonly Label _heading = new() { AutoSize = true, UseMnemonic = false };
    private readonly Label _explanation = new() { AutoSize = true, UseMnemonic = false };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 8) };
    private readonly Button _back = NewButton("< &Back");
    private readonly Button _next = NewButton("&Next >");
    private readonly Button _cancel = NewButton("Cancel");

    private readonly Dictionary<Type, WizardPage> _pages = [];
    private List<WizardPage> _sequence = [];
    private int _index;

    public WizardForm(WizardModel model)
    {
        _model = model;
        Text = Title;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 540);
        MinimumSize = new Size(600, 480);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        AcceptButton = _next;
        CancelButton = _cancel;

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = SystemColors.Window,
            ForeColor = SystemColors.WindowText,
            Padding = new Padding(16, 12, 16, 12),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _heading.Font = new Font(Font.FontFamily, Font.Size + 3, FontStyle.Bold);
        _heading.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _explanation.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _explanation.Margin = new Padding(3, 4, 3, 0);
        header.Controls.Add(_heading, 0, 0);
        header.Controls.Add(_explanation, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 10),
        };

        // Added right to left; the tab order is set to read left to right.
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_next);
        buttons.Controls.Add(_back);
        _cancel.Margin = new Padding(12, 3, 3, 3);
        _back.TabIndex = 0;
        _next.TabIndex = 1;
        _cancel.TabIndex = 2;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_content, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        header.TabIndex = 2;
        _content.TabIndex = 0;
        buttons.TabIndex = 1;
        Controls.Add(root);

        _back.Click += (_, _) => GoBack();
        _next.Click += (_, _) => GoNext();
        _cancel.Click += (_, _) => Close();
        FormClosing += OnClosing;

        // Every page is built before the window shows, so they are all scaled for the screen together.
        foreach (var page in new WizardPage[]
        {
            new WelcomePage(model), new MaintenancePage(model), new UpdatePage(model), new FoldersPage(model), new AdminScreenPage(model),
            new WebCertificatePage(model), new PrinterAccountPage(model), new ModulesPage(model), new UpdatesPage(model), new ShortcutsPage(model),
            new SummaryPage(model), new UninstallPage(model), new ProgressPage(model), new DonePage(model),
        })
        {
            _pages[page.GetType()] = page;
            page.StateChanged += (_, _) => UpdateButtons();
            page.Completed += (sender, _) =>
            {
                if (ReferenceEquals(sender, Current)) Show(_index + 1);
            };
            _content.Controls.Add(page);
        }

        _sequence = SequenceFor(model.Flow);
        Shown += (_, _) => Show(0);
    }

    /// <summary>True once setup has done what was asked.</summary>
    public bool Succeeded => _model.Result is not null;

    private WizardPage Current => _sequence[_index];

    private static Button NewButton(string text) =>
        new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(88, 0), Padding = new Padding(6, 2, 6, 2) };

    private WizardPage Page<T>()
        where T : WizardPage => _pages[typeof(T)];

    private List<WizardPage> SequenceFor(WizardFlow flow)
    {
        WizardPage[] questions =
        [
            Page<FoldersPage>(), Page<AdminScreenPage>(), Page<WebCertificatePage>(), Page<PrinterAccountPage>(), Page<ModulesPage>(), Page<UpdatesPage>(), Page<ShortcutsPage>(),
        ];
        WizardPage[] apply = [Page<SummaryPage>(), Page<ProgressPage>(), Page<DonePage>()];
        return flow switch
        {
            WizardFlow.Install => [Page<WelcomePage>(), .. questions, .. apply],
            WizardFlow.Maintenance => [Page<MaintenancePage>()],
            WizardFlow.Modify => [Page<MaintenancePage>(), .. questions, .. apply],
            WizardFlow.Repair => [Page<MaintenancePage>(), .. apply],
            WizardFlow.Update => [Page<UpdatePage>(), .. apply],
            WizardFlow.Modules => [Page<ModulesPage>(), .. apply],
            _ => _model.State is not null && _sequence.Contains(Page<MaintenancePage>())
                ? [Page<MaintenancePage>(), Page<UninstallPage>(), Page<ProgressPage>(), Page<DonePage>()]
                : [Page<UninstallPage>(), Page<ProgressPage>(), Page<DonePage>()],
        };
    }

    private void Show(int index)
    {
        foreach (var page in _pages.Values) page.Visible = false;
        _index = index;
        var current = Current;
        _heading.Text = current.Heading;
        _explanation.Text = current.Explanation;
        current.Visible = true;
        current.Entering();
        UpdateButtons();

        // Keyboard users land on the page's first control, not on a button.
        if (!current.SelectNextControl(current, forward: true, tabStopOnly: true, nested: true, wrap: false)) _next.Select();
    }

    private void UpdateButtons()
    {
        var current = Current;
        _back.Enabled = _index > 0 && current.CanGoBack && !current.IsWorking && !current.IsLast;
        _next.Text = current.IsLast ? "&Finish" : current.NextCaption;
        _next.Enabled = current.CanGoNext && !current.IsWorking;
        _cancel.Text = current.IsWorking ? "&Stop" : "Cancel";
        _cancel.Visible = !current.IsLast;
    }

    private void GoBack()
    {
        if (_index > 0) Show(_index - 1);
    }

    private void GoNext()
    {
        var current = Current;
        if (current.IsLast)
        {
            current.Leaving();
            Close();
            return;
        }

        if (!current.Leaving()) return;

        // The maintenance page decides which pages follow it.
        if (current is MaintenancePage) _sequence = SequenceFor(_model.Flow);
        Show(_index + 1);
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        var current = Current;
        if (current.IsLast) return;

        if (current.IsWorking)
        {
            e.Cancel = true;
            var stop = MessageBox.Show(
                this,
                "Setup is in the middle of its work. Stopping now can leave ScanRelay half set up; running setup again will finish the job.\n\nStop anyway?",
                Title,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (stop == DialogResult.Yes) current.CancelWork();
            return;
        }

        // Before Install is pressed nothing has been changed, so there is nothing to lose but the answers.
        if (current is ProgressPage || _index == 0) return;
        var leave = MessageBox.Show(this, "Close setup? Nothing has been changed on this server.", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        e.Cancel = leave != DialogResult.Yes;
    }

    /// <summary>Opens an address in the default browser.</summary>
    public static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"The browser could not be opened. Go to {url} yourself.\n\n{e.Message}", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
