# Printer modules

Support for a printer comes from a module. A model with no module cannot be
added.

| Module | Covers | State |
|--------|--------|-------|
| `scanrelay-sample` | The mock printer in `/tests/MockDevices` | Built and tested. It exists to prove the module system and to be copied. It drives no real printer. |
| `kyocera-ecosys` | ECOSYS M8124cidn | **Working on a real printer.** A sync pushed the list, a scan at the panel reached OneDrive. See below. |
| `brother-ads` | ADS-4900W | **One profile saved, scanned to and cleared on a real scanner from the probe tool. A push from the app is not confirmed yet.** See below. |
| `canon-imageclass` | imageCLASS MF642C/643C/644C and MF650C Series; MF751Cdw II not tried yet | **Reading and loading work on a real MF642C and MF650C Series from the probe tool. A scan with the copied password is not confirmed yet.** See below. |

## The devices the modules were built on

| Make | Model | Firmware | Notes |
|------|-------|----------|-------|
| Kyocera | ECOSYS M8124cidn | 2P4_S000.002.204 | In the family the spec's Kyocera module covers. |
| Canon | imageCLASS MF642C | Main controller 10.05 | **Not** an imageRUNNER ADVANCE or imageFORCE. See below. |
| Canon | imageCLASS MF650C Series | Not recorded | Same Remote UI and file layout as the MF642C. |
| Canon | imageCLASS MF751Cdw II | — | Not tried yet. |
| Brother | ADS-4900W (a scanner) | Main firmware 3.20 | |

The Brother printer first named, the HL-3170CDW, has no scanner, so it cannot
have a module. The Brother module is for the ADS scanner.

**The Canon models changed the Canon plan.** The spec's Canon method (build
Canon's address book CSV and upload it on the import page, or copy it with
Device Information Distribution) is the imageRUNNER ADVANCE method. The
imageCLASS MF machines are a different product line with a different Remote
UI, and the format of their address book file is not documented. The module's
method was worked out from real exports and the real Remote UI pages.

## Kyocera: what is known

The printer has a network service on port 9091 (HTTPS) and 9090 (HTTP), at
`/ws/km-wsdl/setting/address_book`. KYOCERA Net Viewer uses it. Kyocera does
not document it.

- **Reading the address book is known.** Two operations,
  `create_personal_address_enumeration` then `get_personal_address_list`, are
  shown in public write-ups. Each entry comes back with its name, its SMB
  server, path, port, sign-in name and password.
- **It works end to end on a real printer.** On an ECOSYS M8124cidn
  (firmware 2P4_S000.002.204), through the installed app: a sync pushed the
  list of people, a document was scanned at the panel to a person's entry, and
  it arrived in their OneDrive. Before that, a one-contact write test added,
  renamed and deleted a contact at number 150 and confirmed the other 18
  contacts were exactly as before. Not yet tried on the real printer: joiners
  and leavers over several syncs, and the nightly drift check.
- **What an entry looks like, from the real printer.** One contact holds a
  name, a number, and email, FTP, SMB, i-fax and fax details together. The SMB
  part is a server name with no backslashes, a path, port 445, a sign-in name
  and a password.
- **What the module owns.** Contacts whose folder ends in a person's id and
  that have no fax or FTP destination. Each one also carries the person's own
  email address, so the printer's scan to email works from the same contact.
  That is a setting and is on by default. It is new and has not been tried on
  a real printer yet. Nothing else is changed or
  removed, whatever its number. New contacts get numbers from a reserved range,
  100 to 199 by default; the range is a per-printer setting. The M8124cidn
  accepts number 150; its upper limit is not known.
- **Sign-in.** The M8124cidn tested had sign-in for the service switched off
  (firmware 2P4_S000.002.204). The module cannot sign in yet. With sign-in on,
  it reports that plainly and changes nothing.
- **One-touch keys** live in a different part of the service and are not
  managed.
- **A file route exists.** Net Viewer can import an address book file. One
  public tool writes that file (root element `DeviceAddressBook_v5_2`), but the
  password in it must be in Net Viewer's own encoded form, which is not
  documented either.
- **The service is open when the printer's sign-in is off.** The M8124cidn
  tested answered a read of the whole address book, stored passwords
  included, with no sign-in, because sign-in was switched off. Anyone who can
  reach ports 9090 and 9091 can do the same. Until the module can sign in,
  limit those ports on the network to IT's computers and the ScanRelay server.

`tools/KyoceraProbe` checks all of this against a real printer. It only reads,
and it removes names, addresses, paths, sign-in names and passwords from its
report:

```
dotnet run --project tools/KyoceraProbe -- <printer address> --netviewer "<Net Viewer install folder>"
```

The first write to a real printer is a separate, deliberate step. It adds one
test contact, renames it, deletes it, and checks that every other contact is
exactly as it was. Back up the address book first.

```
dotnet run --project tools/KyoceraProbe -- <printer address> --write-test --number 150
```

## Canon imageCLASS: what is known

Built on an MF642C (main controller 10.05) and run on an MF650C Series too.
With `tools/CanonProbe`, both have had their address book read and a changed
one loaded back. **Not confirmed yet:** a scan to an entry carrying the copied
password, which is the proof that the password is the right one.

**The file**

- **Two export formats, one of them useful.** The Remote UI can export the
  address book as a Canon Address Book file (`.abk`): plain text, one block
  per entry, names and folder details in Base64. It carries no passwords, so
  the module does not use it.
- **The module uses the configuration file (`.dcm`).** It is a gzip-compressed
  tar archive holding two XML files in clear text: one describing the export,
  one holding the address book. Only the passwords inside are encrypted, each
  as a hex value. How they are encrypted is not known; a look with known
  passwords matched no common scheme. `modules/canon/imageclass/DcmFile.cs`
  reads and writes the file, and writes a real export back byte for byte.
- **One entry is one destination.** Unlike Kyocera, an email address and a
  shared folder are separate entries. ScanRelay creates the shared folder
  entry only; the setting that adds each person's email address applies to
  printers whose entries hold both.
- **Names are cut to 16 characters** by the printer.
- **A load replaces the whole address book and renumbers it.** An entry left
  out of the file is gone afterwards, and the rest are numbered 1, 2, 3
  whatever the file said. So the module sends the whole book, changes only
  its own entries, and recognises its entries by their folder path, never by
  number.

**The password**

- **Reuse, not decrypt.** Every entry ScanRelay creates uses the same share
  password. IT makes one shared folder entry on the printer by hand, named
  exactly `ScanRelay`, with the printer account and its password. The module
  copies that entry's stored password onto the entries it creates, and never
  needs to know how the printer encrypts it.
- **A copied password is accepted, as far as can be seen.** The printer shows
  a password as set on an entry made this way. That it is the right password
  is what a real scan will prove.
- **The template entry leaves the printer at the first push.** It is only
  there to be read. Its user name and stored password are kept, encrypted,
  with the printer's settings. A later read uses the same file password as the
  first, so the kept password can still be loaded, and keeps it when the
  entry is no longer on the printer. To change the share password, make the
  `ScanRelay` entry again with the new one and read again.

**Reading and loading**

- **The address book is read once, then every push is built from that copy.**
  The printer refuses a load for some minutes after an export, so a push
  cannot start with one. On the Printers page, in the printer's own form,
  "Read the address book from the printer" signs in as System Manager, exports
  the book and stores it encrypted with the printer. A push takes that copy,
  replaces ScanRelay's own entries, and loads it. An entry added at the
  printer after the read is lost at the next push, so read again after
  changing the address book by hand.
- **How the export works.** The Export page's form is posted with a one-time
  token. The printer answers with an "Exporting..." page holding a small form;
  the answer to that form is the Export page carrying a one-time address, and
  the file comes from that address.
- **How the load works.** The Import page's choices are posted with a one-time
  token, and the printer says where to send the file. The file must be sent
  the way a browser sends a form: .NET's own form writer quotes the boundary
  and leaves the field name unquoted, and the printer refuses that with
  result 9.
- **A load that finishes is not yet a load that worked.** The printer answers
  the uploaded file itself, and that answer is the result. Asking the result
  address afterwards without a code answers "Import is complete" whatever
  happened. The module takes the upload's own answer, counts only a result
  page the printer marks as normal, and reports anything else with the
  printer's words.
- **A load that is refused because the printer is busy is asked again** every
  15 seconds for up to 3 minutes, which covers a push straight after a read
  or after the nightly check, which also exports.
- **A load that works ends with the printer saying it is restarting.** Whether
  it really restarts after every load has not been reported.

**Settings and models**

- **Where its settings live.** On the Printers page, per printer: the System
  Manager ID and PIN go in the printer's admin sign-in boxes, and the template
  entry's name and the address book PIN, if the printer has one, are under
  "Values from the module".
- **MF650C Series.** Its pages, file layout and load flow are the same as the
  MF642C's. It answered on http only until TLS was turned on for its Remote
  UI; over http the module value `secure` must be `false`, and the System
  Manager PIN and the export then cross the network unencrypted, so TLS is the
  better setting.
- **`tools/CanonProbe`** runs the steps against a real printer and records
  what it answers, with names and passwords left out.

## Brother ADS scanners: what is known

Built for an ADS-4900W (main firmware 3.20), id `brother-ads`. On a real
scanner, with `tools/BrotherProbe`: one profile was saved, a scan to it
reached the share, and the profile was cleared again. **A push from the app
itself has not been tried yet.**

- **One Scan to Network profile per person.** The scanner has 25 profile
  slots, each a Network (shared folder), FTP, SFTP, HTTP(S) or SharePoint
  profile. Twenty-five is the limit for everything, so the scanner gets its
  own small group or assigned people.
- **Which profiles are ScanRelay's.** A Network profile whose folder path ends
  in a person's id. Every other profile, of any kind, is left as it is. New
  people go into slots with no name; an unnamed slot of another kind is made
  a Network one first. A leaver's profile is cleared.
- **The password is typed in as it is.** Nothing has to be made by hand on the
  scanner first and nothing has to be read from it, unlike the Canon.
- **Names are cut to 14 characters.**
- **How its Web Based Management works,** from its own page scripts and a real
  read: the pages are empty shells. Sign-in is one password posted to
  `/auth/login`, over https only. The scanner refuses it with 401 unless the
  visitor has opened one of its pages first, so the module opens the front
  page before signing in; three runs on a real scanner showed this. The profile list is XML at `/ft/scan_profile`, and the kind of
  all 25 slots is posted back to it in one go. One Network profile is XML at
  `/ft/scan_tosharefoler=N&&` (the spelling is the scanner's). Saving posts
  every setting of the profile back to the same address as `name=value` pairs
  joined by `&&`; the module sends the scan settings back as it read them and
  sets only the name, folder, account and password. Brother does not document
  any of this.
- **The scanner refuses a save** while it is in use, and while its Auto Start
  Scan mode is on. Both are reported in the scanner's own terms.
- **`tools/BrotherProbe`** reads a real scanner, and with `--add` saves one
  profile and reads it back. Passwords, PINs and profile names are left out
  of its report.
- **Version 0.1.0 did not load in app 0.2.7.** It was the first module published apart from an app release, and was built against a newer copy of the module contract than that app has. 0.1.1 is the same module built against the fixed-version contract.
- **The scanner leaves an empty test file in the folder.** It writes one to check it can reach the share and cannot delete it again, because the printer account may only create and write. From app 0.2.8 an empty file that stays empty for ten minutes is removed quietly; before that it became a failed job.
- **Proven on the real scanner:** a save as the module sends it is taken, a
  scan to the saved profile arrives, and clearing all four fields removes a
  profile.
- **Not known yet:** in what form the scanner hands a saved password back
  (which decides whether a changed share password is noticed by itself), and
  whether changing a slot's kind from FTP to Network was exercised: the
  profile saved in the test may have gone into the one slot that already was
  a Network one.

## How the vendor modules were worked out

None was written against guesses. Two of the three methods are not documented
by the vendor, and code written blind passes its own tests and fails at the
printer. Each module started from a real device: a small probe tool
(`tools/KyoceraProbe`, `tools/CanonProbe`, `tools/BrotherProbe`) reads the
device and records what it answers with names and passwords left out, the
module and its mock are written from that record, and one deliberate write is
tried with the probe before the module is released.

Without a module for a printer the app is still usable: scans are delivered,
and the address book is kept by hand. Each person's scan folder is
`\\<server>\<share>\<their user ID>`, written with the printer account. The
People page lists who is on the list, and the Runs page shows who joined and
left at each sync.

## Rules for a module in this repo

- Methods a vendor does not document are marked `unofficial: true` in the
  module data, with a `method` line that says where the method stands. The
  admin screen shows both.
- No vendor software is included. A tool the module needs is listed under
  `prerequisites` and downloaded by the user.
- A module only touches the scope it reserves on the printer. Fax numbers and
  other existing entries are left alone, and a test proves it.
