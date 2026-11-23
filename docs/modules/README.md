# Printer modules

Support for a printer comes from a module. A model with no module cannot be
added.

| Module | Covers | State |
|--------|--------|-------|
| `scanrelay-sample` | The mock printer in `/tests/MockDevices` | Built and tested. It exists to prove the module system and to be copied. It drives no real printer. |
| `kyocera-ecosys` | ECOSYS M8124cidn | **Working on a real printer.** A sync pushed the list, a scan at the panel reached OneDrive. See below. |
| Brother | — | **Dropped from the first version.** No Brother that scans is in use. |
| Canon | imageCLASS MF642C, MF650C, MF751Cdw II | **Not started.** After Kyocera. |

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
