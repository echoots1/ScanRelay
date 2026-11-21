# Writing a module

A module teaches ScanRelay how to keep one printer family's address book in
step with the list of people. The project does not take outside code, so a
module for another printer is built in a fork and installed as a custom
module.

## Which devices qualify

A device can have a module if it has all three:

- An address book or destination list that an administrator manages.
- Scan to a network folder over SMB 2 or later, FTP or SFTP.
- A way to change that address book remotely: a web interface, a network
  service or a vendor tool.

## What a module is

One folder under `/modules/<vendor>/<module>` with two things in it:

- `module.yaml`, the model data.
- An adapter: a .NET class that implements `IDeviceAdapter` from
  `src/ModuleSdk`.

Start by copying `/modules/scanrelay/sample`. It drives the mock printer in
`/tests/MockDevices` and is small enough to read in one sitting.

## The model data

```yaml
id: kyocera-taskalfa          # lower-case letters, digits and hyphens
version: 1.0.0
vendor: Kyocera
family: TASKalfa
models: []                    # exact model names, added as each is tested
windows_names: []             # model names as Windows reports them; a trailing * matches any ending
status: custom                # official | custom
sdk_version: 1
app_versions:
  min: 0.1.0                  # max is optional
adapter: Your.Module.dll      # the adapter assembly in the package
unofficial: true              # true when the vendor does not document the method
method: Uses the printer's network service. Not documented by the vendor.
scan:
  protocols: [smb, ftp]       # smb | ftp | sftp
  smb_versions: ["2.0", "3.0"]   # SMB 1 is refused
  share_rights: [create, write]  # add read, list or delete only if the printer cannot finish a write without them
address_book:
  max_entries: null           # from the vendor manual
  managed_range: [900, 999]   # the scope this module reserves
  one_touch: true
prerequisites: []             # anything the admin must install first
tested_firmware: []
options:                      # per-printer values and their defaults, shown when a printer is set up
  port: "80"
```

Check it at any time:

```
dotnet run --project tools/ModuleTool -- check modules/<vendor>/<module>/module.yaml
```

`share_rights` matters for security. The printer account's password sits in
every address book entry, so the design assumes it will leak. By default the
account can create and write files and nothing else: it cannot list, read or
delete. Ask for more only when a real printer fails without it.

## The adapter

```csharp
public interface IDeviceAdapter
{
    string Id { get; }
    Task<ProbeResult> ProbeAsync(Device device, CancellationToken ct = default);
    Task<IReadOnlyList<Entry>?> ReadManagedAsync(Device device, CancellationToken ct = default);
    Task<ApplyResult> ApplyAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken ct = default);
    Task<ImportFile> ExportAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken ct = default);
}
```

The class takes a `ModuleContext` in its constructor, or nothing. The context
gives it a shared `HttpClient` and a `Log` method.

Three rules:

1. **`ApplyAsync` makes the printer's managed entries equal to `wanted`.** It
   is always given the whole list, never single add or remove commands. It
   must be safe to run twice: the second run changes nothing.
2. **Only touch the scope the module reserves.** One address list, one number
   range, whatever the printer offers. Everything else on the printer is left
   exactly as it was.
3. **Report failures; do not throw them.** Return a result with a reason a
   person can act on, such as "The printer refused the admin sign-in." The
   service survives a module that throws, but the message will be worse.

`ReadManagedAsync` returns `null` when the printer cannot be read back. When
it can, the nightly drift check uses it to repair printers changed by hand.

`ExportAsync` builds the file IT applies by hand when a push fails. Leave the
share password out of it.

Never log passwords or file contents.

## Testing without hardware

Add a fake of the printer's interface under `/tests/MockDevices`, the way
`MockPrinter` fakes the sample's. Give it entries the module does not own, and
test that they survive a push. `tests/Core.Tests/ModuleTests.cs` shows the
tests every module should pass.

A mock proves the code does what you think the printer wants. Only a real
machine proves the printer agrees. Record what you tested on under
`tested_firmware`.

## Building and installing

```
dotnet publish modules/<vendor>/<module> -c Release -o out/<module>
dotnet run --project tools/ModuleTool -- pack out/<module> out/packages
```

The result is one file, `<id>-<version>.srmod`. On the server, run the
ScanRelay installer, open the Modules step, and choose to add a custom module
from a file. The installer warns that the module is not from the project and
has not been checked by it, and you must confirm.

A custom module is marked Custom on the Modules page and in the audit log. The
update check ignores it. To update one, add the newer file the same way.

A fork that ships its own installer can build it with its own repository
address and its own signing key (`catalog/signing-key.pub.pem`). Its modules
then install as official for that fork.
