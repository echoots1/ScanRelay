using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using ScanRelay.Core.Install;
using ScanRelay.ModuleSdk;

namespace ScanRelay.Core.Modules;

/// <summary>A module as the service sees it after start-up.</summary>
/// <param name="Problem">Why it cannot be used, or null when it loaded.</param>
public sealed record LoadedModule(ModuleData? Data, InstalledModule Installed, IDeviceAdapter? Adapter, string? Problem)
{
    public string Id => Installed.Id;
    public bool IsCustom => Installed.Status != "official";
    public bool Usable => Adapter is not null && Problem is null;
}

/// <summary>Loads the installed modules once, at start-up, and hands out their adapters.</summary>
public sealed class ModuleHost(InstallConfig install, TrustedKeys keys, HttpClient http, ILogger<ModuleHost> logger)
{
    private readonly Lock _lock = new();
    private IReadOnlyList<LoadedModule>? _modules;

    public static Version AppVersion { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(ModuleHost).Assembly).GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 1, 0);

    public IReadOnlyList<LoadedModule> Modules
    {
        get
        {
            lock (_lock)
            {
                return _modules ??= LoadAll();
            }
        }
    }

    public LoadedModule? Find(string id) => Modules.FirstOrDefault(m => m.Id == id);

    /// <summary>The usable modules that cover a model name as Windows reports it.</summary>
    public IReadOnlyList<LoadedModule> Match(string? windowsModelName) =>
        [.. Modules.Where(m => m.Usable && m.Data!.MatchesWindowsName(windowsModelName))];

    private IReadOnlyList<LoadedModule> LoadAll()
    {
        if (string.IsNullOrWhiteSpace(install.ModulesFolder) || !Directory.Exists(install.ModulesFolder)) return [];
        var store = new ModuleStore(install.ModulesFolder, keys);
        var loaded = new List<LoadedModule>();
        IReadOnlyList<InstalledModule> installed;
        try
        {
            installed = store.Read();
        }
        catch (ModuleException e)
        {
            logger.LogError("{Problem}", e.Message);
            return [];
        }

        foreach (var record in installed)
        {
            var module = Load(store, record);
            loaded.Add(module);
            if (module.Problem is null)
            {
                logger.LogInformation("Loaded module {Id} {Version} ({Status})", record.Id, record.Version, record.Status);
            }
            else
            {
                logger.LogError("Module {Id} was not loaded: {Problem}", record.Id, module.Problem);
            }
        }

        return loaded;
    }

    private LoadedModule Load(ModuleStore store, InstalledModule record)
    {
        ModuleData? data = null;
        try
        {
            var path = Path.Combine(store.Folder, record.File);
            if (!File.Exists(path)) return new(null, record, null, "Its package file is missing. Run the installer's Repair.");

            // The app only loads what it can verify: the file must be the one the installer
            // recorded, and an official module must still carry the project's signature.
            if (!ModulePackage.Sha256(path).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new(null, record, null, "Its package file has changed since it was installed. It was not loaded.");
            }

            var sigPath = path + ModulePackage.SignatureExtension;
            var signature = File.Exists(sigPath) ? File.ReadAllText(sigPath) : null;
            data = ModulePackage.ReadData(path);
            if (record.Status == "official" && !keys.Verify(File.ReadAllBytes(path), signature))
            {
                return new(data, record, null, "Its signature is not the project's. It was not loaded.");
            }

            if (data.SdkVersion != ModuleSdkInfo.Version)
            {
                return new(data, record, null, $"It was built for module SDK {data.SdkVersion}; this app uses {ModuleSdkInfo.Version}. Pushes to its printers are paused until a matching module is installed.");
            }

            if (!data.WorksWithApp(AppVersion))
            {
                return new(data, record, null, $"It has no version for app {AppVersion}. Pushes to its printers are paused until a matching module is installed.");
            }

            var adapter = CreateAdapter(path, data);
            if (adapter.Id != data.Id) return new(data, record, null, $"Its adapter says it is \"{adapter.Id}\", not \"{data.Id}\".");
            return new(data, record, adapter, null);
        }
        catch (Exception e) when (e is ModuleException or IOException or BadImageFormatException or ReflectionTypeLoadException or TypeLoadException or MissingMethodException or TargetInvocationException)
        {
            return new(data, record, null, (e as TargetInvocationException)?.InnerException?.Message ?? e.Message);
        }
    }

    private IDeviceAdapter CreateAdapter(string packagePath, ModuleData data)
    {
        var context = new PackageLoadContext(packagePath, data.Id);
        var assembly = context.LoadFromPackage(data.Adapter);
        var type = assembly.GetTypes().FirstOrDefault(t => typeof(IDeviceAdapter).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            ?? throw new ModuleException($"{data.Adapter} contains no adapter.");
        var moduleContext = new ModuleContext(data.Id, http, line => logger.LogInformation("[{Module}] {Line}", data.Id, line));
        var instance = type.GetConstructor([typeof(ModuleContext)]) is { } withContext
            ? withContext.Invoke([moduleContext])
            : Activator.CreateInstance(type);
        return (IDeviceAdapter)instance!;
    }

    /// <summary>Loads a module's assemblies straight from its verified package, apart from the SDK the service shares with it.</summary>
    private sealed class PackageLoadContext(string packagePath, string name) : AssemblyLoadContext("module:" + name)
    {
        public Assembly LoadFromPackage(string entryName)
        {
            using var zip = ZipFile.OpenRead(packagePath);
            var entry = zip.GetEntry(entryName) ?? throw new ModuleException($"The package does not contain {entryName}.");
            using var buffer = new MemoryStream();
            using (var stream = entry.Open()) stream.CopyTo(buffer);
            buffer.Position = 0;
            return LoadFromStream(buffer);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == typeof(IDeviceAdapter).Assembly.GetName().Name) return null;
            using var zip = ZipFile.OpenRead(packagePath);
            return zip.GetEntry(assemblyName.Name + ".dll") is null ? null : LoadFromPackage(assemblyName.Name + ".dll");
        }
    }
}
