# ScanRelay plan

Status: draft for review. Nothing here is built. The [spec](spec.pdf) says what
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

## 2. Technical decisions

| # | Decision | Choice | Notes |
|---|----------|--------|-------|
| D1 | Runtime | .NET 10 (current LTS), self-contained publish | No runtime to install on the server. |
| D2 | Process shape | One process: Windows service hosting Kestrel, Blazor Server and the background workers | `/src/Service` and `/src/Admin` are separate projects in one host. |
| D3 | Installer | **To decide.** WiX bundle with a custom setup UI, or a plain MSI plus an elevated setup program for the parts MSI cannot do | The first gives the "Modify" experience the spec describes. The second is far less work. Recommend a short spike in M1 before committing. |
| D4 | Database | SQLite through EF Core, migrations run at start | Backup before upgrade is a file copy. |
| D5 | Secrets at rest | Windows DPAPI, machine scope, with the data folder's rights as the second layer | Covers printer admin sign-ins and the printer account password. |
| D6 | Graph | Microsoft Graph SDK, app-only with certificate | One thin interface in front of it so tests use a fake. |
| D7 | Admin sign-in | OpenID Connect against the tenant, group check done server-side with the app's own Graph access | Avoids group claims and their size limit. |
| D8 | Module packaging | One zip per module: `module.yaml`, adapter assembly, signature file | Each module loads in its own load context. |
| D9 | Module signing | ECDSA P-256 over the package hash; public key compiled into the installer and the service | Key custody is an open question. The service re-checks at load, not only the installer at install. |
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

## 3. Milestones

These follow the spec's build order. Each ends in something usable.

### M0. Foundations
- Settle G1 to G4, D3, the licence and the name.
- Solution skeleton, CI that builds and runs tests on every change, secret scanning on.
- Database schema, first draft: settings, people, printers, jobs, runs, pushes, printer entries, audit log.

### M1. Installer and core service
- Installer: folders, local printer account, hidden share, rights, both certificates, service registration, firewall rule, summary before changes, uninstall.
- Service host, HTTPS, localhost-only until sign-in is set up.
- First run: Entra connection (create or by hand), consent wait, health check, admin group.
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
| Licence: Apache 2.0 or MIT | Apache 2.0. Its `NOTICE` rule is the "credit is kept" requirement the spec asks for, and it carries a patent grant. | M0 |
| Final name for app and repo | — | M0, before anything is public |
| Helpdesk role needed? | Keep it. It is cheap once roles exist and hard to add later. | M1 |
| Accept tenant-wide `Files.ReadWrite.All`? | Yes, see G7. | M1 |
| Alert mailbox, and restrict `Mail.Send` to it? | Restrict. Setup shows the exact command and the health check reports whether the restriction is in place. | M2 |
| Printers not installed on the server | Allow by address, as the spec's "Adding a printer" already says. | M4 |
| Module signing key: who holds it, where | Not on a personal computer. A protected secret in the release pipeline is the minimum; a hardware key or key vault is better. | M4 |
| Exact models and firmware of the first printers | — | M5 to M7 |
| Kyocera: does the network service method work, and is it on by default | — | M5 |
| Brother: does the tool cover scan-to-folder profiles; how many slots | — | M6 |
| Canon: `direct` or `host`; which address list | — | M7 |

Added by this plan: G1, G2, G3, G4, G9 and D3 above.

## 5. Biggest risks

1. **The printer methods are unproven.** Two of the three are undocumented or unofficial. M1 to M4 are worth having without them, which is why the order is what it is.
2. **Installer effort (G6, D3).** Easy to underestimate. Spike it early.
3. **Tenant policy.** Conditional Access, consent restrictions and Exchange policies vary. Every setup step needs a by-hand path and a health check that says exactly what is missing.
4. **Printer behaviour against a locked-down share (G5).** Needs real machines well before M5.
