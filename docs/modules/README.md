# Printer modules

Support for a printer comes from a module. A model with no module cannot be
added.

| Module | Covers | State |
|--------|--------|-------|
| `scanrelay-sample` | The mock printer in `/tests/MockDevices` | Built and tested. It exists to prove the module system and to be copied. It drives no real printer. |
| Kyocera | TASKalfa and ECOSYS multifunction models | **Not started.** |
| Brother | Models supported by the Mass Deployment Tool | **Not started.** |
| Canon | imageRUNNER ADVANCE and imageFORCE | **Not started.** |

## The first printers

These are the models the first modules are for. Firmware versions are still to come.

| Make | Model | Notes |
|------|-------|-------|
| Kyocera | ECOSYS M8124cidn | In the family the spec's Kyocera module covers. |
| Canon | imageCLASS MF642C series | **Not** an imageRUNNER ADVANCE or imageFORCE. See below. |
| Canon | imageCLASS MF650C series | Same. |
| Canon | imageCLASS MF751Cdw II | Same. |

No Brother model is on the list yet.

**The Canon models change the Canon plan.** The spec's Canon method (build
Canon's address book CSV and upload it on the import page, or copy it with
Device Information Distribution) is the imageRUNNER ADVANCE method. The
imageCLASS MF machines are a different product line with a different Remote
UI. They can save and load their address book from the Remote UI, but the file
format is not documented in what has been found so far, and Canon's manuals
warn that a saved address book cannot always be loaded on another model. So
the Canon module needs its method worked out from a real export file before
anything is built.

## Why the three vendor modules are not started

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
