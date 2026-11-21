# Privacy

ScanRelay runs on your own Windows server. The project does not run any
service that your installation talks to, and receives nothing from it.

## What the app connects to

- **Microsoft Graph, for the tenant you connect it to.** It reads group
  members and user names, uploads scans to each person's OneDrive, and sends
  alert emails from the mailbox you choose.
- **GitHub, for the update check.** Once a day it reads this project's latest
  release and module list. It sends nothing about your installation. The
  check can be turned off in the installer and in Settings, and then the app
  never contacts GitHub.
- **Your printers,** to keep their address books up to date.

It connects to nothing else. No usage data is collected.

## What is stored on your server

- The names, sign-in names and directory IDs of the people who can scan.
- A record of each scan: who, when, size, result. Kept for 90 days by default.
- Each scanned file, for 7 days by default, so a failed upload can be re-run.
- An audit log of changes made in the admin screen, with who made them.
- Printer admin sign-ins and the printer account's password, encrypted.

Logs record who, when, size and result. They never record file contents.

Everything above lives in the app's data folder, which only the service and
administrators of the server can open. Uninstalling asks whether to keep it.
