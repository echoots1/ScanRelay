# Development

Needs the .NET 10 SDK. The app itself is Windows-only, but everything except
the installer builds, runs and is tested on Linux and macOS too.

```
dotnet build ScanRelay.slnx
dotnet test ScanRelay.slnx
```

## Layout

| Folder | What it is |
|--------|------------|
| `src/Core` | Everything the service does: jobs, sync, modules, Graph, settings, alerts, updates. No web code. |
| `src/Service` | The process: hosting, sign-in, background work. |
| `src/Admin` | The admin screen, as Blazor components. |
| `src/ModuleSdk` | The contract a module implements. |
| `src/Installer` | The Windows installer. |
| `modules/` | One folder per printer family. |
| `tools/ModuleTool` | Packs and signs modules. |
| `tests/MockCloud` | A stand-in for Entra sign-in, Graph, OneDrive, mail and GitHub releases. |
| `tests/MockDevices` | Fake printers. |
| `tests/DevHarness` | Runs the whole app against the mocks. |
| `tests/e2e` | The browser test of the admin screen. |

## Seeing the app without a tenant or a printer

```
dotnet run --project tests/DevHarness
```

Open `http://localhost:5080`. It starts at first-run setup, with a mock
Microsoft 365 holding a few made-up people and groups, and a mock printer.
Sign-ins are approved automatically as "Alex Admin". When setup waits for
permissions to be approved, play the Entra admin:

```
curl -X POST http://localhost:5081/consent
```

Other things the control address does, all as `POST`:

| Address | Effect |
|---------|--------|
| `/scan/Ann%20Example` | A printer writes a scan for that person |
| `/scan-to-folder/<name>` | A scan arrives in a folder no person owns |
| `/sign-in-as/Harper%20Helpdesk` | The next sign-in is that person |
| `/graph/fail/3` | Graph fails the next three requests |
| `/printer/broken/true` | The printer answers with errors |
| `/group/All%20Staff/clear` | Everyone leaves the group |
| `/release/v9.0.0` | A new release appears on the mock GitHub |

`GET /state` shows what is in each mock OneDrive, on the printer, and in the
mail outbox.

## The browser test

With the harness running:

```
npm install --no-save playwright
npx playwright install chromium
node tests/e2e/admin-screen.mjs screenshots
```

It walks through setup, adding a printer, a sync, a scan, a failure and
re-run, the change guard, and what the Helpdesk role can and cannot see.

## What the tests can and cannot show

The mock Microsoft 365 checks what the real one checks: the certificate
signature on the service's sign-in, consent before application permissions
work, single-tenant registration, name clashes on upload, and upload in
pieces. The real Graph and sign-in code runs against it unchanged.

That is not the same as a real tenant. Before a release, the checklist in
[server-test.md](server-test.md) is run on a real Windows Server against a
real tenant.

## Changing the database

```
dotnet tool restore
dotnet ef migrations add <Name> --project src/Core -o Data/Migrations
```

A test fails if the model and the migrations disagree.

## The installer

`src/Installer` is the window; `src/Installer/Engine` is everything it does,
with no window code, so it can be tested. It only runs on Windows.

For testing it has a silent mode, which the build pipeline uses on every
change:

```
ScanRelaySetup.exe /quiet /answers answers.json
ScanRelaySetup.exe /quiet /uninstall [/keepdata]
```

`tests/windows/Test-Install.ps1` installs, checks everything the installer
promises, repairs and uninstalls. It creates and removes a local account, a
share, a firewall rule and a service, so run it on a build machine or a test
server only. `tests/windows/Capture-Wizard.ps1` saves a picture of each page
of the wizard without installing anything.
