# ScanRelay plan

Status: the app works end to end on a real server, a real tenant and one real
printer. On 2026-11-23 a sync read an Entra group, pushed the list to a Kyocera
ECOSYS M8124cidn, a document was scanned at the panel to a person's entry, and
it arrived in that person's OneDrive. That is one printer model, one tenant and
one person's scan; the rest of [the server test](server-test.md) has not been
run. The Canon module is not started. [What is verified](#what-is-verified)
says where each part stands, and [the overnight log](overnight-log.md) lists
the questions answered along the way.

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
| D3 | Installer | **Changed:** our own setup program, `ScanRelaySetup.exe`, not a WiX bundle and MSI | It does everything the spec's installer does, registers under Windows Installed apps with Modify and Uninstall, and has a silent mode so it can be tested on a build machine on every change. A WiX bundle would have added a second engine to debug blind, for a label. It is not a "standard Windows installer package" in the MSI sense; see the overnight log. |
| D4 | Database | SQLite through EF Core, migrations run at start | Backup before upgrade is a file copy. |
| D5 | Secrets at rest | Windows DPAPI, machine scope, with the data folder's rights as the second layer | Covers printer admin sign-ins and the printer account password. |
| D6 | Graph | **Changed:** plain HTTP calls to Graph through our own small client, app-only with certificate. No Graph SDK and no Microsoft sign-in library. | It lets the real sign-in and Graph code run in tests against a mock that checks the certificate signature, consent and upload rules. |
| D7 | Admin sign-in | OpenID Connect against the tenant, group check done server-side with the app's own Graph access | Avoids group claims and their size limit. |
| D8 | Module packaging | One zip per module: `module.yaml`, adapter assembly, signature file | Each module loads in its own load context. |
| D9 | Module signing | ECDSA P-256 over the package hash; public key compiled into the installer and the service | **Decided:** the private key is a GitHub Actions secret, used only by the release pipeline. The service re-checks at load, not only the installer at install. |
| D10 | Logging | **Changed:** the built-in .NET logging, with a small rolling-file writer of our own and the Windows Event Log | One less dependency. One file per day, kept for 30 days. No file contents, as the spec requires. |
| D11 | Tests | xUnit; a mock Microsoft 365 under `/tests/MockCloud`; mock printers under `/tests/MockDevices`; a browser test of the admin screen under `/tests/e2e` | |
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

These follow the spec's build order.

| Milestone | State |
|-----------|-------|
| M0. Foundations | Built. |
| M1. Installer and core service | Built. The service, first-run setup, sign-in with roles, audit log and Settings are tested. The installer passes a real install, repair and uninstall on a Windows build machine; its screens have not been looked at by a person. |
| M2. Jobs | Built and tested: intake, upload, retries, retention, re-run, alerts, Jobs page, Dashboard. |
| M3. People and sync | Built and tested: groups, manual people, exclusions, folders, preview, change guard, Runs and People pages. |
| M4. Module system | Built and tested with the sample module and mock printer: SDK, packaging, signing, catalog, module host, Printers and Modules pages, update check, drift check. |
| M5. Kyocera module | **Working on a real ECOSYS M8124cidn** (firmware 2P4_S000.002.204): sync, push, scan at the panel, file in OneDrive. Not yet tried: joiners and leavers over time, the drift check, and sign-in to the printer's service. See [modules](modules/README.md). |
| M6. Brother module | **Dropped from the first version.** No Brother that scans is in use. |
| M7. Canon module | **Built against a mock; not yet run against a printer.** First model: imageCLASS MF642C. The file formats come from real exports and the requests from the real Remote UI pages. See [modules](modules/README.md). |
| M8. Public release | Docs, module template, mock devices, build checks and the release pipeline are in place. No release has been made. The signing key does not exist yet; see [releasing](releasing.md). |

<a id="what-is-verified"></a>
## What is verified

"Tested" below means automated tests that run on every change, on Windows and
Linux build machines. "Real" means it has been seen working on the owner's own
server, tenant and printer, at least once.

| Part | How it has been checked | What has not |
|------|-------------------------|--------------|
| Scan intake, upload, retries, retention | Tested against the mock. **Real:** a scan made at the printer arrived in the right OneDrive. | Large files, long scans, failures and re-runs on the real server. Retention over days. |
| Sign-in to Graph with the certificate | Tested against the mock. **Real:** works on a real tenant. | — |
| Creating the app registration | Tested against the mock. **Real:** setup connected a real tenant and the permissions were approved. | Which route was used (code or by hand) was not recorded. Running setup a second time. |
| Admin sign-in and roles | Tested against the mock, running the real sign-in flow. | Not confirmed on the real tenant yet: finishing setup by signing in, and the Helpdesk role. |
| Directory sync, change guard, drift check | Tested against the mocks. **Real:** a sync read a real group and created people; a member with a locked OneDrive is skipped. | The change guard, joiners and leavers, and the drift check on the real tenant. |
| Module system | The sample module is packed, signed, installed, loaded and pushed to the mock printer in tests. **Real:** the Kyocera module was installed from a file as a custom module and pushed to a real printer. | An official, signed module installed from GitHub. |
| Admin screen | A browser test walks through setup, a printer, a sync, a scan, a failure, the change guard and the Helpdesk role | Nobody has used it by hand. |
| Installer | On every change a Windows Server build machine installs it silently and runs 56 checks: the service and its account, HTTPS, the setup gate, folder rights, both certificates, the printer account and its sign-in denials, the share, the firewall rule, Installed apps, a custom module loading, a repair that keeps the certificate and password, and a clean uninstall | The wizard screens: nobody has looked at them or clicked through them. Modify and Update through the screens. Moving folders. A real server with real policies. |
| The share's rights | On the same build machine the test connects to the share as the printer account: writing a scan works; listing, reading back and deleting are refused; the service then picks the file up | A real printer. Many printers probe a folder before writing, and may need more than create and write. |
| Alert emails | Tests check what is sent and to whom | Delivery through a real mailbox. |
| Vendor modules | **Kyocera ECOSYS M8124cidn: real.** A push put people on the printer and a scan from the panel reached OneDrive. | Other Kyocera models. Canon is not built. Brother is dropped. |

The checklist for a real server is [server-test.md](server-test.md).

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
