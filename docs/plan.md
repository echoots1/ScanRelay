# ScanRelay plan

Status: milestone 0 is built. Everything after it is planned, not built. The [spec](spec.pdf) says what
the app does; this document says how it gets built, in what order, and what
has to be settled first.

## 1. Gaps found in the spec

These are places where the spec as written does not work, or leaves out
something that changes the design. Each needs a decision before the milestone
that depends on it.

| # | Gap | Why it matters | Proposed answer | Blocks |
|---|-----|----------------|-----------------|--------|
| G1 | **Setup needs an app before it can create the app.** The sign-in link and code (device code flow) must be started with an existing client ID. | Without one, first run cannot sign anyone in to create the registration. | Use the Microsoft Graph Command Line Tools public client, which exists in every tenant. Alternative: the project publishes its own multi-tenant "ScanRelay Setup" app, which is nicer to read on the consent screen but is one more thing the project must own and protect. | M1 |
| G2 | **Device code sign-in is often blocked.** Many tenants now block it with Conditional Access. | Setup would fail at step 1 in exactly the well-run tenants. | Offer a browser sign-in on the server (`http://localhost` redirect) as the first choice, device code as the second, and the by-hand values as the third. | M1 |
| G3 | **A low-privilege service cannot write to the machine certificate store.** The spec has setup (the service) make the Graph certificate and keep the key in the machine store. | The call fails with access denied. | The installer creates the Graph certificate and gives the service account read access to its key, the same way it handles the web certificate. Setup only uploads the public half. Renewal from Settings then needs an elevated helper or an installer Repair. | M1 |
| G4 | **Who creates the per-person folders and sets rights on them.** The service must create `Inbox\<user id>` and, per the spec, widen the printer account's rights when a module needs it. | Needs permission to change rights on `Inbox`, which a low-privilege account lacks by default. | The installer gives the service account full control of the data folder, including changing rights. Share-level rights stay fixed; only folder rights change. | M1, M3 |
| G5 | **Write-only folders and real printers.** Many printers test a destination by listing, or write a temp file and rename or delete it. | "Create and write, no list, read or delete" may fail on most models, making the stricter default the exception. | Keep the strict default, but treat `share_rights` in module data as the truth, and make "Test the connection" on the Printers page prove a real write. Verify on the first real machines before M2 is called done. | M2 |
| G6 | **The installer does a lot for an MSI.** It lists modules from GitHub, downloads, verifies signatures, picks certificates and shows a summary. | Plain MSI dialogs cannot do this well. This is the biggest unknown in effort. | See decision D3. | M1, M4 |
| G7 | **Per-OneDrive scoping is close to impractical.** Granting the app access one OneDrive at a time needs a far stronger permission to do the granting. | Answers the spec's open question. | Accept tenant-wide `Files.ReadWrite.All` and say so plainly in the docs and at consent. | M1 |
| G8 | **Two-second-apart scans, same name.** Covered by the numbered suffix, but a re-run of a job must not create a second copy. | Re-run after a timeout whose upload did in fact succeed would duplicate. | Record the OneDrive item ID on the job; on re-run check for it first. | M2 |
| G9 | **Removing a person "deletes their folder".** The spec's IT section reads as if a OneDrive folder is deleted. | Deleting someone's scans in OneDrive would be a surprise. | Read it as the server `Inbox` folder only. OneDrive is never deleted from. Confirm. | M3 |

### Decided: sign-in during first run

G1 and G2 are settled as follows.

- **Configuring needs no sign-in.** Until setup is finished, the admin screen
  opens straight into the first-run steps with no sign-in at all. There is no
  setup password and no one-time code.
- **The one sign-in is at the app registration step.** An Entra admin signs in
  once, there, so setup can create the registration. Browser sign-in on the
  server is offered first, link and code second, by-hand values third. It uses
  the Microsoft Graph Command Line Tools public client. Setup keeps nothing
  from that sign-in.
- **What protects the open screen** is the spec's existing rule: until sign-in
  is set up, the screen answers only on the server itself. Anyone who can
  reach it is already logged on to the server.
- **The open window closes for good** once the admin group is chosen and one
  member of it has signed in successfully. From then on every page needs
  Entra sign-in. Requiring that first successful sign-in before closing the
  window means a wrong group cannot lock everyone out.
- **Getting back in** if sign-in later breaks (group deleted, registration
  removed) is an installer Repair, which needs administrator rights on the
  server and reopens the local-only setup screen.

## 2. Technical decisions

| # | Decision | Choice | Notes |
|---|----------|--------|-------|
| D1 | Runtime | .NET 10 (current LTS), self-contained publish | No runtime to install on the server. |
| D2 | Process shape | One process: Windows service hosting Kestrel, Blazor Server and the background workers | `/src/Service` and `/src/Admin` are separate projects in one host. |
| D3 | Installer | **Decided: the full installer the spec describes**, as a WiX bundle with a custom setup UI | Version 1 ships the whole spec. Built in two passes: folders, account, share, certificates and service in M1; the Modules step in M4. |
| D4 | Database | SQLite through EF Core, migrations run at start | Backup before upgrade is a file copy. |
| D5 | Secrets at rest | Windows DPAPI, machine scope, with the data folder's rights as the second layer | Covers printer admin sign-ins and the printer account password. |
| D6 | Graph | Microsoft Graph SDK, app-only with certificate | One thin interface in front of it so tests use a fake. |
| D7 | Admin sign-in | OpenID Connect against the tenant, group check done server-side with the app's own Graph access | Avoids group claims and their size limit. |
| D8 | Module packaging | One zip per module: `module.yaml`, adapter assembly, signature file | Each module loads in its own load context. |
| D9 | Module signing | ECDSA P-256 over the package hash; public key compiled into the installer and the service | **Decided:** the private key is a GitHub Actions secret, used only by the release pipeline. The service re-checks at load, not only the installer at install. |
| D10 | Logging | Serilog to a rolling file and the Windows Event Log | No file contents, as the spec requires. |
| D11 | Tests | xUnit; fakes for Graph, the file system clock and devices; mock device servers under `/tests/MockDevices` | |
| D12 | CI | GitHub Actions on Windows runners; release built only by the pipeline | Needed from M1, since the Windows-only parts cannot be built or tested elsewhere. |

### What can be tested where

Most logic is portable and gets fast unit tests: the watcher's "finished file"
rule, the job state machine, retry and retention, the sync diff, the change
guard, module data validation, signature checks.

The Windows-only parts need a real Windows machine: the installer, local
account and share creation, folder rights, the certificate store, the service
itself, listing installed printers. CI covers builds and unit tests; these
parts need a test server and a written manual checklist.

The printer modules need real hardware. Mock devices prove the code does what
we think the printer wants. Only a real machine proves the printer agrees.

### Decided: how version 1 is verified

- Version 1 is the whole spec, not a cut-down first release. It is not called
  done until it has been verified on a real Windows Server.
- Every change builds and runs the automated tests on Windows and Linux.
- Each milestone ends with a full pass on the test server, following a written
  checklist kept in `/docs`. The checklist for a milestone is written with
  that milestone, and every earlier checklist is run again.
- The printer modules are verified on real machines once the models are
  known. Until then they are not started.

### Decided: built for any organisation

Nothing about one tenant, network or printer fleet is built in. Anything that
differs between organisations is a setting, with a safe default, including the
alert mailbox. Restricting `Mail.Send` to that mailbox is done by the
organisation in Exchange Online, so setup explains how and the health check
reports whether it is in place, but the app works either way.

## 3. Milestones

These follow the spec's build order. Each ends in something usable.

### M0. Foundations (built)
- Solution skeleton on .NET 10: `Core`, `ModuleSdk`, `Admin`, `Service`, and two test projects. `Core` is an addition to the spec's layout; it holds the database and shared types that both `Service` and `Admin` use.
- One process serves the admin screen and a `/health` check, and creates and upgrades the database on start.
- Database schema, first draft: settings, people, printers, jobs, runs, pushes, printer entries, audit log.
- The adapter interface from the spec, as a first draft.
- CI builds and tests every change on Windows and Linux, publishes the Windows service build, and scans for secrets.

### M1. Installer and core service
- Installer: folders, local printer account, hidden share, rights, both certificates, service registration, firewall rule, summary before changes, uninstall.
- Service host, HTTPS, localhost-only and open without sign-in until setup is finished.
- First run: Entra connection (one admin sign-in to create the registration, or by hand), consent wait, health check, admin group, first admin sign-in closes the open window.
- Admin sign-in with Admin and Helpdesk roles. Audit log.
- Settings page, export and import without secrets.

**Done when:** a clean Windows Server goes from installer to a signed-in admin on the Settings page, and uninstall leaves nothing behind but the data folder if kept.

### M2. Jobs
- Watcher plus 60-second sweep, finished-file rule, 10-minute timeout.
- Upload: small and large paths, `Documents` by its built-in reference, `Scans` created if missing, naming template, clash suffix, allowed types.
- Retry three times with growing delay. Move to `Store`. Retention for files and job records.
- Jobs page: search, filter, re-run, open file (Admin only, audited).
- Alert emails with grouping. Dashboard, first version.

**Done when:** a file dropped into a hand-made `Inbox\<user id>` folder reaches that person's OneDrive, a forced failure shows on the Jobs page and re-runs cleanly. The app can go live here with address books kept by hand.

### M3. People and sync
- Read groups, filter to enabled accounts with a OneDrive, guests by setting.
- Manual people and exclusions, kept through every sync.
- Wanted state, diff, folder creation and removal.
- Runs page: sync now, preview, change guard with approve or cancel.
- No printers yet, so pushes are recorded against a fake module.

**Done when:** adding and removing a group member creates and removes their folder, and emptying the group stops at the change guard.

### M4. Module system
- Module SDK: `IDeviceAdapter`, entry and device types, module data schema.
- Package format, signing tool, catalog index generation in the release pipeline.
- Installer Modules step: list, download, verify, install, custom module from a file with a warning.
- Module host, load-time verification, Modules page, daily update check.
- Printers page: list installed Windows printers, match to a module, per-printer overrides, test connection, test entry, push now, download import file.
- Nightly drift check for modules that can read back.
- One mock device and one sample module to prove the whole path.

**Done when:** the sample module installs through the installer and a sync pushes entries to the mock device.

### M5 to M7. Kyocera, Brother, Canon
Each one: read the references in full, confirm the method on the real model and firmware, write module data, adapter and mock device, write the per-vendor doc with unofficial methods labelled.

Order stays as the spec has it. Each is blocked by the matching open question below, and none should be started before a real machine is on hand to test against.

### M8. Public release
- Install guide, module author guide and template, privacy statement.
- Release pipeline with checksums, release notes carrying the per-release update steps.
- Update flow tested end to end: stop, back up database, replace, update modules, start, migrate.

## 4. Open questions

From the spec, with a recommendation where there is one.

| Question | Recommendation | Needed by |
|----------|----------------|-----------|
| Licence: Apache 2.0 or MIT | **Decided: Apache 2.0.** `LICENSE` and `NOTICE` are in the repo. | Done |
| Final name for app and repo | **Decided: ScanRelay.** | Done |
| Helpdesk role needed? | **Decided: kept.** | Done |
| Accept tenant-wide `Files.ReadWrite.All`? | **Decided: yes**, see G7. | Done |
| Alert mailbox, and restrict `Mail.Send` to it? | **Decided:** the mailbox is a setting. Restriction is recommended, explained by setup and reported by the health check, not required. | Done |
| Printers not installed on the server | Allow by address, as the spec's "Adding a printer" already says. | M4 |
| Module signing key: who holds it, where | **Decided:** a GitHub Actions secret, used only by the release pipeline. | Done |
| Exact models and firmware of the first printers | To come. | M5 to M7 |
| Kyocera: does the network service method work, and is it on by default | — | M5 |
| Brother: does the tool cover scan-to-folder profiles; how many slots | — | M6 |
| Canon: `direct` or `host`; which address list | — | M7 |

G3, G4 and G9 are decided as proposed in section 1.

## 5. Biggest risks

1. **The printer methods are unproven.** Two of the three are undocumented or unofficial. M1 to M4 are worth having without them, which is why the order is what it is.
2. **Installer effort (G6, D3).** The full installer is the largest single piece of version 1 and can only be tested on Windows.
3. **Tenant policy.** Conditional Access, consent restrictions and Exchange policies vary. Every setup step needs a by-hand path and a health check that says exactly what is missing.
4. **Printer behaviour against a locked-down share (G5).** Needs real machines well before M5.
