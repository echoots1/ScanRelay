# Printer modules

Support for a printer comes from a module. A model with no module cannot be
added.

| Module | Covers | State |
|--------|--------|-------|
| `scanrelay-sample` | The mock printer in `/tests/MockDevices` | Built and tested. It exists to prove the module system and to be copied. It drives no real printer. |
| `kyocera-ecosys` | ECOSYS M8124cidn | **Working on a real printer.** A sync pushed the list, a scan at the panel reached OneDrive. See below. |
| Brother | — | **Dropped from the first version.** No Brother that scans is in use. |
| `canon-imageclass` | imageCLASS MF642C/643C/644C and MF650C Series; MF751Cdw II not tried yet | **Reading and loading work on a real MF642C from the probe tool. A push from the app, and a scan with the copied password, are not confirmed yet.** See below. |

## The first printers

These are the models the first modules are for. Firmware versions are still to come.

| Make | Model | Notes |
|------|-------|-------|
| Kyocera | ECOSYS M8124cidn | In the family the spec's Kyocera module covers. |
| Canon | imageCLASS MF642C series | **Not** an imageRUNNER ADVANCE or imageFORCE. See below. |
| Canon | imageCLASS MF650C series | Same. |
| Canon | imageCLASS MF751Cdw II | Same. |

The only Brother named so far is the HL-3170CDW, which is a printer with no
scanner, so it cannot have a module. No Brother model that scans is on the
list yet.

**The Canon models change the Canon plan.** The spec's Canon method (build
Canon's address book CSV and upload it on the import page, or copy it with
Device Information Distribution) is the imageRUNNER ADVANCE method. The
imageCLASS MF machines are a different product line with a different Remote
UI. They can save and load their address book from the Remote UI, but the file
format is not documented in what has been found so far, and Canon's manuals
warn that a saved address book cannot always be loaded on another model. So
the Canon module needs its method worked out from a real export file before
anything is built.

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

Work has started with the MF642C.

- **The file format is known.** The Remote UI exports the address book as a
  Canon Address Book file (`.abk`). It is plain text: a few comment lines,
  then one block per entry, each with a number (`dn`), a name, and either an
  email address or a shared folder's host, path and user name. Names and
  folder details are Base64. `modules/canon/imageclass/AbkFile.cs` reads and
  writes it, and writes back a real export byte for byte.
- **One entry is one destination.** Unlike Kyocera, an email address and a
  shared folder are separate entries, so a person who should have both needs
  two.
- **The folder password is not exported.** The `pwd` line comes out empty.
  Whether a file with a password in it is accepted on the way in, and in what
  form, is not known yet.
- **Loading a file works, and needs no restart.** The MF642C's Import page
  took a plain address book file once a decryption password was typed, added
  the new entry, and the printer did not need restarting. The entry's password
  was not taken from the plain file, though.
- **The encrypted export is only partly encrypted.** The device configuration
  file (`.dcm`) is a gzip-compressed tar archive holding two XML files in
  clear text: one describing the export, one holding the address book. Only
  the passwords inside are encrypted, each as a hex value. How they are
  encrypted is not known, and a first look with known passwords did not match
  any common scheme.
- **How the module works: reuse, not decrypt.** Every entry ScanRelay creates
  uses the same share password. IT makes one shared folder entry on the
  printer by hand, named exactly `ScanRelay`, with the printer account and its
  password. It never needs to know how the printer encrypts passwords.
- **The address book is read once, then every push is built from that copy.**
  The printer refuses a load for some minutes after an export, so a push
  cannot start with one. On the Printers page, in the printer's own form,
  "Read the address book from the printer" signs in as System Manager, exports
  the book and stores it encrypted with the printer; nothing is uploaded by
  hand. A push takes that copy, replaces ScanRelay's own entries, and loads it.
  Two things follow. An entry added at the printer after the read is lost at
  the next push, so read again after changing the address book by hand. And if
  the share password changes, retype it in the `ScanRelay` entry and read again.
- **A load that finishes is not yet a load that worked.** The first loads
  sent to a real MF642C were not taken, and the module said they were. The
  printer answers the uploaded file itself: when it cannot use the file it
  sends the browser to its result address with a code (9 was seen), and that
  answer is the result. Asking the result address afterwards without a code
  answers "Import is complete" whatever happened. The module now takes the
  upload's own answer, counts only a result page the printer marks as normal,
  and reports anything else with the printer's words.
- **The file is sent the way a browser sends it.** The refused loads were
  sent with .NET's own form writer, which quotes the boundary and writes the
  field name without quotes and an extra `filename*`. The module now writes
  the form as a browser does. Whether that was the cause of result 9 is not
  proven until a load from the module is taken by a real printer.
- **A load from the module's code is taken by a real MF642C** (main controller
  10.05), run with `tools/CanonProbe`: read, then load from the saved copy
  with one entry added. Sending the file the way a browser does was what the
  refused loads lacked. A push from the app itself, and a scan to an entry
  with the copied password, are the remaining proof.
- **The template entry leaves the printer at the first push.** It is only
  there to be read. Its user name and stored password are kept, encrypted,
  with the printer's settings. A later read uses the same file password as the
  first, so the kept password can still be loaded, and keeps it when the
  entry is no longer on the printer. To change the share password, make the
  `ScanRelay` entry again with the new one and read again.
- **MF650C Series: read and load work on a real one,** run with
  `tools/CanonProbe`. Its pages, its file layout and its load flow are the
  same as the MF642C's. It answered on http only until TLS was turned on for
  its Remote UI; over http the module value `secure` must be `false`, and the
  System Manager PIN and the export cross the network unencrypted, so TLS is
  the better setting. A first sign-in was refused when tried over the wrong
  one of the two. Its firmware version was not recorded.
- **A load that works ends with the printer saying it is restarting.**
- **A load that is refused because the printer is busy is asked again** every
  15 seconds for up to 3 minutes, which covers a push straight after a read
  or after the nightly check, which also exports.
- **Where its settings live.** On the Printers page, per printer: the System
  Manager ID and PIN go in the printer's admin sign-in boxes, and the template
  entry's name and the address book PIN, if the printer has one, are under
  "Values from the module".
- **Built and tested against a mock; the read is proven on a real printer, the
  load is not yet.** Sign-in and export work on a real MF642C (main controller
  10.05): its "Exporting..." page holds a small form, the answer to that form
  is the Export page carrying a one-time address, and the file comes from that
  address. Loading a file back follows the real Import page but has not been
  run from the module yet. `tools/CanonProbe` runs the steps against a real
  printer and records what it answers.
- **A copied encrypted password is accepted, as far as can be seen.** A file
  with one entry's encrypted password copied onto a new entry loaded, and the
  printer shows a password as set on the new entry. That it is the right
  password has not been proven yet; a scan to a real share with a copied
  password will prove it.
- **A load replaces the whole address book and renumbers it.** An entry left
  out of the file was gone afterwards, and the remaining entries were numbered
  1, 2, 3 whatever the file said. So the module must send the whole book,
  change only its own entries, and recognise its
  entries by their folder path, never by number.
- **`modules/canon/imageclass/DcmFile.cs` reads and writes the file,** and
  writes a real export back byte for byte.
- **Names are cut to 16 characters** by the printer.
- **Whether a load replaces the whole address book or adds to it** is being
  tested alongside.
- **How the Import page works.** It posts the choices to
  `/cgi/m_import_dev_mng_import.cgi` with a one-time token from the page, is
  told where to send the file, sends it, then asks
  `/json/dcm_import_result.json` until the import is done. The sign-in page
  has not been seen yet.
- **Email entries are not wanted on Canon.** ScanRelay creates the shared
  folder entry only. The setting that adds each person's email address applies
  to printers whose entries hold both, such as Kyocera.

## Brother ADS scanners: what is known

Being built for an ADS-4900W (main firmware 3.20). Nothing has been written
to a scanner yet.

- **One Scan to Network profile per person.** The scanner has 25 profile
  slots, each a Network (shared folder), FTP, SFTP, HTTP(S) or SharePoint
  profile. A Network profile holds a name of up to 14 characters, a folder
  path, a user name and a password. Twenty-five slots is the limit, so the
  scanner gets its own group or assigned people.
- **The password is typed in as it is.** The profile page takes the share
  password in plain form, so nothing has to be made by hand on the scanner
  first and nothing has to be read from it, unlike the Canon.
- **How its Web Based Management works,** from its own page scripts: the pages
  are empty shells. Sign-in is one password posted to `/auth/login`, over
  https only. The profile list is XML at `/ft/scan_profile`. One Network
  profile is XML at `/ft/scan_tosharefoler=N&&` (the spelling is the
  scanner's), and saving posts the values back to the same address as
  `name=value` pairs joined by `&&`. Brother does not document this.
- **`tools/BrotherProbe` reads a real scanner** and records what it answers,
  with passwords, PINs and profile names left out. It changes nothing.
- **Not known yet:** what the profile XML really holds, how a slot's kind is
  set to Network, and whether an emptied profile is how one is removed.

## Why the vendor modules were not built blind

Each one depends on a method that has not been tried on a real machine, and
two of the three methods are not documented by the vendor. Writing them
against guesses would produce code that passes its own tests and fails at the
printer. They are waiting on:

1. The exact model and firmware of the first printer of each make.
2. A real machine to test against.
3. A full read of the references listed at the end of the spec.

Until then the app is usable as the spec describes after build step 2: scans
are delivered, and address books are kept by hand. Each person's scan folder
is `\\<server>\<share>\<their user ID>`, written with the printer account.
The People page lists who is on the list, and the Runs page shows who joined
and left at each sync.

## Rules for a module in this repo

- Methods a vendor does not document are marked `unofficial: true` in the
  module data, with a `method` line that says where the method stands. The
  admin screen shows both.
- No vendor software is included. A tool the module needs is listed under
  `prerequisites` and downloaded by the user.
- A module only touches the scope it reserves on the printer. Fax numbers and
  other existing entries are left alone, and a test proves it.
