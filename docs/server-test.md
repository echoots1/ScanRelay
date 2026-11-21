# Server test

The automated tests run the app against a mock Microsoft 365 and a mock
printer. This checklist is the part they cannot do: a real Windows Server, a
real tenant, and real printers. Run all of it before each release.

Write down the Windows Server version, the app version, and for each printer
its model and firmware.

## 1. Install

- [ ] The download matches `SHA256SUMS.txt`.
- [ ] The installer shows a summary before changing anything, and Cancel there leaves the server untouched.
- [ ] After install, the first-run screen opens in the browser on the server.
- [ ] The service `ScanRelay` is running under `NT SERVICE\ScanRelay`, not an administrator.
- [ ] "Installed apps" lists ScanRelay with Modify and Uninstall.
- [ ] The data folder is hidden. A normal user cannot open it. An administrator can.
- [ ] The local printer account exists, is in no groups, and cannot sign in at the console or over Remote Desktop.
- [ ] The share exists and is hidden. `Store`, the database and the logs are not reachable over the network.
- [ ] From another computer, before setup is finished, `https://<server>:<port>/` answers "not set up yet" and nothing else.

## 2. The share, as the printer account

From another computer, connect to the share as the printer account
(Settings, Printer account shows the password).

- [ ] Creating a file in a person's folder works.
- [ ] Listing the share, or a person's folder, is refused.
- [ ] Reading a file back is refused.
- [ ] Deleting a file is refused.
- [ ] Connecting with SMB 1 is refused.

## 3. First run, against the real tenant

- [ ] "Create the app registration" shows a link and code; signing in creates a single-tenant registration named ScanRelay with a certificate and no client secret.
- [ ] The registration asks for exactly the five permissions in the install guide.
- [ ] Before consent, the screen says which permissions are still waiting. After consent it notices on its own.
- [ ] Running the Entra setup again finds the same registration and does not create a second one.
- [ ] The by-hand path works: with a registration made in the portal from the listed values, "Save and check" succeeds.
- [ ] The health check is all OK once the groups are chosen.
- [ ] Signing in as a member of the admin group finishes setup. The audit log shows it.
- [ ] After that, the screen asks for sign-in on the server too, and works from another computer.
- [ ] Someone outside both groups is told they have no access.
- [ ] A Helpdesk member sees Dashboard, Jobs and Runs only, can re-run, and is not offered scan files.

## 4. Scans

- [ ] A one-page scan arrives in `Documents/Scans` in the right OneDrive within a few seconds of the printer finishing.
- [ ] It works for a person whose OneDrive is not in English.
- [ ] Two scans in the same second get different names.
- [ ] A long scan (50 pages or more) is not uploaded until the printer has finished writing it.
- [ ] A file larger than 250 MB uploads.
- [ ] With the server's network cable out, a scan shows as retrying, then Failed, and an alert email arrives. With the cable back, Re-run delivers it.
- [ ] A scan made while the service is stopped is delivered when it starts.
- [ ] A file dropped in a folder no person owns shows as Failed with a clear reason.
- [ ] An Admin can open a scan from the Jobs page, and the audit log records it.
- [ ] Each printer model writes successfully with only create and write rights. If one does not, note exactly what it needed.

## 5. People and sync

- [ ] Sync now creates a folder for each member of the sync group.
- [ ] Adding someone to the group, then Sync now, creates their folder. Removing them removes it once it is empty.
- [ ] A disabled account, a guest, and an account with no OneDrive are left out, and the run says who was skipped.
- [ ] A person added by hand stays through a sync. An excluded person stays excluded.
- [ ] Preview shows the changes and makes none.
- [ ] Emptying a test group stops at the change guard, emails the admins, and changes nothing until approved.

## 6. Printers (once a module for the model exists)

- [ ] The Add list shows the printers installed on the server and matches the model to the module.
- [ ] Test connection reports the model and firmware.
- [ ] Push test entry puts "ScanRelay test" on the panel. A scan to it shows on Jobs as a test scan.
- [ ] Push now fills the address book. Existing entries the app does not own, such as fax numbers, are untouched.
- [ ] Pushing twice changes nothing the second time.
- [ ] A joiner and a leaver reach the panel at the next sync.
- [ ] With the printer off, the push fails with a readable reason, an email arrives, and the import file downloads.
- [ ] An entry deleted at the panel is back after the nightly drift check (if the module can read the printer back).

## 7. Alerts and settings

- [ ] "Save and send a test email" arrives from the chosen mailbox.
- [ ] With `Mail.Send` limited in Exchange Online to the sender mailbox, alerts still arrive.
- [ ] Changing a setting is in the audit log with the old and new values.
- [ ] Export, then import on a second server, restores the settings without secrets.

## 8. Repair, update, uninstall

- [ ] Running the installer again offers Modify, Repair and Uninstall.
- [ ] Modify can change the port and the web certificate; the admin screen then answers on the new address.
- [ ] Adding a module through Modify makes its models available on the Printers page.
- [ ] Repair with "Reopen the local setup screen" lets an administrator back in on the server without sign-in.
- [ ] Installing a newer version over this one keeps settings, people, jobs and custom modules, and leaves a database backup.
- [ ] Scans made during the update are delivered afterwards.
- [ ] Uninstall removes the service, share, firewall rule, printer account and program folder, and asks about the data folder.

## 9. Restart and failure

- [ ] After a server restart, the service starts on its own and the admin screen answers.
- [ ] Killing the service process: Windows restarts it.
- [ ] The log file in the data folder has no scan contents and no passwords.
