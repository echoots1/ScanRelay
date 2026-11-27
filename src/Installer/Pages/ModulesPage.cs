using ScanRelay.Core.Modules;

namespace ScanRelay.Installer.Pages;

/// <summary>
/// Which printer modules to have. The list comes from the project's signed catalog on
/// GitHub; modules can also be added from files, with a warning for ones the project has not checked.
/// </summary>
internal sealed class ModulesPage : WizardPage
{
    private const string Title = "ScanRelay Setup";

    private enum Source
    {
        Catalog,
        InstalledOnly,
        CustomFile,
        OfficialFile,
    }

    /// <summary>One line of the list.</summary>
    private sealed record Item(string Id, string Text, Source Source, bool Installed, string? File = null)
    {
        public override string ToString() => Text;
    }

    private readonly Label _status;
    private readonly CheckedListBox _list;
    private readonly Button _retry;
    private readonly IReadOnlyList<InstalledModule> _installed;
    private CatalogIndex? _catalog;
    private bool _loading;
    private bool _asked;

    public ModulesPage(WizardModel model)
        : base(model)
    {
        _installed = SetupEngine.InstalledModules(model.State);
        AddText("A module teaches ScanRelay how to manage the address book of one family of printers. Tick the ones for the printers you have. Modules can be added or removed later by running setup again.");
        _status = AddText("");
        AddCaption("Printer &modules:");
        _list = Add(new CheckedListBox { CheckOnClick = true, IntegralHeight = false, AccessibleName = "Printer modules" }, grows: true);

        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        var custom = NewButton("Add a &custom module from a file...", 0);
        var official = NewButton("Add an &official module from a file...", 1);
        _retry = NewButton("&Try GitHub again", 2);
        custom.Click += (_, _) => AddCustomFile();
        official.Click += (_, _) => AddOfficialFile();
        _retry.Click += (_, _) => LoadCatalog();
        buttons.Controls.AddRange([custom, official, _retry]);
        Add(buttons).Margin = new Padding(0);
        AddText("Use the official-module button on a server with no internet: download the module file and its .sig file from the project's releases page on another computer and bring both here.");
    }

    public override string Heading => "Printer modules";

    public override string Explanation => "Choose the modules for your printers.";

    public override bool CanGoNext => !_loading;

    public override void Entering()
    {
        if (!_asked) LoadCatalog();
    }

    public override bool Leaving()
    {
        var ticked = _list.CheckedItems.Cast<Item>().ToList();
        var unticked = _list.Items.Cast<Item>().Except(ticked).ToList();
        var answers = Model.Answers;
        answers.Modules = [.. ticked.Where(i => i.Source == Source.Catalog).Select(i => i.Id)];
        // A module being replaced, from the list or a file, is not also removed.
        var replaced = ticked.Where(i => i.Source != Source.InstalledOnly).Select(i => i.Id).ToHashSet();
        answers.RemoveModules = [.. unticked.Where(i => i.Installed && !replaced.Contains(i.Id)).Select(i => i.Id).Distinct()];
        answers.CustomModuleFiles = [.. ticked.Where(i => i.Source == Source.CustomFile).Select(i => i.File!)];
        answers.OfflineModuleFiles = [.. ticked.Where(i => i.Source == Source.OfficialFile).Select(i => i.File!)];

        // Each custom file was agreed to in its own warning dialog when it was added.
        answers.AcceptCustomModules = answers.CustomModuleFiles.Count > 0;
        return Check("module");
    }

    private static Button NewButton(string text, int tabIndex) =>
        new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 1, 6, 1), TabIndex = tabIndex };

    private async void LoadCatalog()
    {
        if (_loading) return;
        _asked = true;
        _loading = true;
        _retry.Enabled = false;
        _status.Text = $"Getting the module list from GitHub ({Model.Answers.Repository})...";
        OnStateChanged();
        string? problem = null;
        try
        {
            _catalog = await Task.Run(() => Model.Engine.FetchCatalogAsync(Model.Answers, Model.State));
        }
        catch (Exception e) when (e is ModuleException or HttpRequestException or OperationCanceledException)
        {
            _catalog = null;
            problem = e.Message;
        }

        if (IsDisposed) return;
        _loading = false;
        _retry.Enabled = true;
        _retry.Visible = _catalog is null;
        _status.Text = problem is null
            ? "This list is the project's own, checked against its signature. Each module is checked again when it is downloaded."
            : $"The module list could not be fetched from GitHub. {problem}\nYou can continue without it and add modules later, or add them from files now. Installed modules are kept.";
        Fill();
        OnStateChanged();
    }

    /// <summary>Rebuilds the list, keeping whatever the admin has ticked or unticked so far.</summary>
    private void Fill()
    {
        var before = _list.Items.Cast<Item>().ToDictionary(i => (i.Source, i.Id), i => _list.CheckedItems.Contains(i));
        var files = _list.Items.Cast<Item>().Where(i => i.Source is Source.CustomFile or Source.OfficialFile).ToList();
        var app = ModuleHost.AppVersion;
        var items = new List<(Item Item, bool Ticked)>();

        var listed = new HashSet<string>();
        if (_catalog is not null)
        {
            foreach (var group in _catalog.Modules.Where(m => Works(m, app)).GroupBy(m => m.Id).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var entry = group.OrderByDescending(m => Version.TryParse(m.Version, out var v) ? v : new Version(0, 0)).First();
                // A custom module with the same id keeps its own line below, so it is never removed just by leaving this one unticked.
                var have = _installed.FirstOrDefault(m => m.Id == entry.Id && m.Status == ModuleStatus.Official);
                var custom = _installed.Any(m => m.Id == entry.Id && m.Status != ModuleStatus.Official);
                var note = custom ? "  -  ticking this replaces the custom module of the same name"
                    : have is null ? ""
                    : have.Version == entry.Version ? "  -  installed"
                    : $"  -  installed {have.Version}, update to {entry.Version} available";
                var text = $"{entry.Vendor} {entry.Family}  ({entry.Id} {entry.Version}){(entry.Unofficial ? "  -  uses a method the vendor does not document" : "")}{note}";
                if (have is not null) listed.Add(entry.Id);
                items.Add((new Item(entry.Id, text, Source.Catalog, have is not null), have is not null || Model.Answers.Modules.Contains(entry.Id)));
            }
        }

        foreach (var have in _installed.Where(m => !listed.Contains(m.Id)).OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var why = have.Status != ModuleStatus.Official ? "custom, not checked by the project"
                : _catalog is null ? "official"
                : "official, but the list has no version of it for this release";
            items.Add((new Item(have.Id, $"{have.Id} {have.Version}  -  installed ({why}); untick to remove", Source.InstalledOnly, true), true));
        }

        items.AddRange(files.Select(f => (f, true)));

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var (item, ticked) in items)
        {
            _list.Items.Add(item, before.TryGetValue((item.Source, item.Id), out var was) ? was : ticked);
        }

        _list.EndUpdate();
    }

    private static bool Works(CatalogEntry entry, Version app)
    {
        try
        {
            return entry.WorksWithApp(app);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private string? PickFile(string title)
    {
        using var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = $"ScanRelay modules (*{ModulePackage.Extension})|*{ModulePackage.Extension}",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private void AddCustomFile()
    {
        if (PickFile("Add a custom module") is not { } file) return;
        ModuleData data;
        try
        {
            data = ModulePackage.ReadData(file);
        }
        catch (Exception e) when (e is ModuleException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"{file} cannot be used as a module.\n\n{e.Message}", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"\"{data.Id}\" {data.Version} ({data.Vendor} {data.Family}) is a custom module.\n\n"
            + "It is NOT from the ScanRelay project and has NOT been checked by it. A module is code that runs inside the ScanRelay service. "
            + "It can see the printers' admin sign-ins and everything else the service can.\n\n"
            + "Only install it if you trust whoever gave it to you. It will be marked Custom wherever it appears.\n\n"
            + "Install this custom module?",
            "Custom module",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        AddFileItem(new Item(data.Id, $"{data.Id} {data.Version}  -  CUSTOM, from {file}; not checked by the project", Source.CustomFile, false, file));
    }

    private void AddOfficialFile()
    {
        if (PickFile("Add an official module") is not { } file) return;
        var signatureFile = file + ModulePackage.SignatureExtension;
        try
        {
            if (!File.Exists(signatureFile))
            {
                MessageBox.Show(this, $"An official module needs its signature file beside it:\n\n{signatureFile}\n\nDownload both files from the project's releases page.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Checked now so a bad file is refused here, not half-way through the install. It is checked again when installed.
            var (data, _) = new ModuleStore(Model.Answers.ModulesFolder, TrustedKeys.BuiltIn()).Check(file, File.ReadAllText(signatureFile), allowCustom: false);
            AddFileItem(new Item(data.Id, $"{data.Id} {data.Version}  -  official, from {file}", Source.OfficialFile, false, file));
        }
        catch (Exception e) when (e is ModuleException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"{file} was not added.\n\n{e.Message}\n\nIf this is a custom module, use \"Add a custom module from a file\" instead.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void AddFileItem(Item item)
    {
        // One file per module: adding it again replaces the earlier choice.
        foreach (var old in _list.Items.Cast<Item>().Where(i => i.File is not null && i.Id == item.Id).ToList()) _list.Items.Remove(old);
        _list.Items.Add(item, isChecked: true);
        _list.SelectedItem = item;
    }
}
