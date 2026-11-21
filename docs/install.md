# Install guide

ScanRelay installs like any off-the-shelf program: run the installer, answer a
few questions, and the first-run screen opens. After that everything is done
in the web admin screen. There are no settings files to edit and no command
line.

## Before you start

- **A Windows Server** that the printers can reach over the network, and that
  can reach Microsoft 365. An administrator account on it.
- **An Entra admin**, for a few minutes, with:
  - Application Administrator or Cloud Application Administrator, to create
    the app registration.
  - Privileged Role Administrator or Global Administrator, to approve its
    permissions. This can be a second person.
- **Three decisions:**
  - Which Entra group holds the IT staff who run this (the admin group).
  - Which Entra group holds everyone who can scan (the default sync group).
  - Which mailbox alert emails are sent from. Optional.

Everyone who scans needs a OneDrive, because that is where their scans go.

## Run the installer

Download `ScanRelaySetup-<version>.exe` from the project's releases page and
check it against `SHA256SUMS.txt`:

```
Get-FileHash .\ScanRelaySetup-<version>.exe -Algorithm SHA256
```

The installer is not signed yet, so Windows shows an unknown publisher
warning. Run it as an administrator. It asks:

| Question | Default | Notes |
|----------|---------|-------|
| Where to install the program | `C:\Program Files\ScanRelay` | |
| Where to keep the data folder | `C:\ProgramData\ScanRelay` | A hidden location that staff never browse. Scans wait here for up to 7 days, so disk encryption on this volume is recommended. |
| Address and port for the admin screen | This server's name, port 8443 | The address IT will type into a browser. |
| Web certificate | Self-signed | Or pick one already on the server. See below. |
| Printer account name | `scanrelay-printer` | A local account. The installer creates its password and stores it encrypted. |
| Which printer modules to install | None | Ticked from the list on GitHub. A custom module can be added from a file. |
| May the app check GitHub for updates | Yes | Can be changed later in Settings. |

Before it changes anything, the installer shows what it will create on the
server: the local account, the hidden share, the firewall rule and the
service. Nothing is done until you confirm.

It then:

1. Copies the program files.
2. Creates the data folder. Only the service and administrators can open it.
3. Creates the local printer account. It cannot sign in interactively and has
   rights nowhere else.
4. Shares the `Inbox` folder as a hidden share. Only the printer account has
   access, and it can only create and write files: it cannot list, read or
   delete. A leaked printer password cannot be used to read anyone's scans.
5. Creates two certificates in the machine certificate store: one for the
   admin screen, and one the service uses to prove who it is to Entra.
6. Installs the modules you ticked.
7. Registers the Windows service under its own low-privilege account, set to
   restart on failure.
8. Adds a firewall rule for the admin screen's port, if you ticked the box.
9. Starts the service and opens the first-run screen.

**The share.** The installer does not change who can reach the share over the
network. Limiting it to the printer network is a firewall task for IT.

## First run

The first visit must come from the server itself. Until setup is finished
there is no sign-in, so the admin screen answers only on the server.

1. **Connect to Entra.** The screen shows a link and a code. An Entra admin
   opens the link in any browser, enters the code and signs in. Setup creates
   the app registration and uploads the certificate. It then shows a consent
   link, which someone with the right role opens and approves. The screen
   notices on its own when that is done.

   If your organisation prefers to create the registration by hand, or blocks
   sign-in by code, choose "I will create it by hand". The screen lists the
   exact values to enter.
2. **Choose the admin group.**
3. **Pick the default sync group.**
4. **Choose the alert mailbox.** Optional.
5. **Add the first printer,** if a module for it is installed.
6. **Scan one page** and watch it arrive.
7. **Sign in.** When a member of the admin group signs in successfully, setup
   is finished. From then on every page needs sign-in, and the screen can be
   opened from other computers. Until that sign-in works, the screen stays
   open on the server, so a wrong group cannot lock everyone out.

### The permissions setup asks for

| Permission | Type | Used for |
|------------|------|----------|
| `GroupMember.Read.All` | Application | Reading who is in the sync groups and the admin group |
| `User.Read.All` | Application | Finding people by name and locating their OneDrive |
| `Files.ReadWrite.All` | Application | Uploading scans to each person's OneDrive |
| `Mail.Send` | Application | Sending alert emails to the admin group |
| `User.Read` | Delegated | Signing IT in to the admin screen |

Two of these are broad, and you should know it before you approve:

- **`Files.ReadWrite.All` covers every OneDrive in the tenant.** The app only
  ever writes scans into the scan folder of people on its list, but the
  permission itself is not limited to that.
- **`Mail.Send` can send as any mailbox unless Exchange Online limits it.**
  Settings shows the command that limits the app to the one sender mailbox.
  Doing so is recommended.

The service signs in with a certificate whose private key stays in the
machine certificate store and cannot be exported. There is no client secret.

## The web certificate

Browsers show a warning for a certificate they do not trust. Sign-in through
Entra works either way.

- **Self-signed, the default.** Works at once. Browsers warn until IT's
  computers are told to trust it.
- **An existing certificate on the server.** Pick it in the installer, at
  install or later through Modify. No warning when browsers already trust its
  issuer.
- **Behind a reverse proxy.** Keep the default certificate and point the
  proxy at the service. The proxy must pass the original host name through,
  and the admin screen's address in the installer must be the address people
  type.

A renewed or replaced certificate is assigned the same way. The admins get an
email 30 days before a certificate expires.

## Going live without printer modules

The app is usable before any printer module exists for your printers. Scans
are delivered; address books are kept by hand.

After a sync, every person on the list has a folder. On each printer, add an
address book entry per person:

- **Path:** `\\<server>\<share name>\<the person's Entra user ID>`
- **User:** `<server>\<printer account name>`
- **Password:** the printer account's password. An Admin can show it under
  Settings, Printer account. Showing it is written to the audit log.

The People page lists who is on the list, with each person's folder path. The
Runs page shows who joined and left at each sync.

## Day to day

| Task | Where |
|------|-------|
| New person | Add them to the Entra group, or add them by hand on the People page |
| Leaver | Remove them from the group. Their entries are removed and their server folder is deleted once empty. Nothing in their OneDrive is touched. |
| "My scan never arrived" | Jobs: find the scan, read the error, press Re-run |
| A printer did not update | Runs: read the error, press Re-run |
| A sync was stopped by the change guard | Runs: read what it would remove, then Approve or Cancel |

## Changing, repairing, updating, removing

- **Change or repair:** run the installer again, or use Modify under Windows
  installed apps. This is how modules are added, updated or removed. It is
  also the only way to change the folder locations, the share, the port, the
  web certificate or the printer account, because those need administrator
  rights.
- **Locked out:** if sign-in stops working, for example because the admin
  group was deleted, run the installer's Repair and tick "Reopen the local
  setup screen". The admin screen then opens without sign-in on the server
  itself until an admin signs in again.
- **Update:** download the new installer and run it on the server as an
  administrator. It stops the service, backs up the database, replaces the
  program files, updates the installed modules and starts the service. Scans
  are not lost: printers keep writing to the share while the service is
  stopped, and the waiting files are processed when it starts. Reinstalling
  the previous version restores the database backup.
- **Uninstall:** removes the service, the share and the firewall rule, and
  asks whether to keep the data folder.

## When something is wrong

Open Settings and run the health check. It says exactly what is missing.
Recent errors are on the Dashboard. For deeper problems there is a rolling
log file in the data folder under `Logs`, and the Windows Event Log.
