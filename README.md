# ScanRelay

> Working name. Status: **planning** — there is no code yet.

A person picks their name on the printer panel, scans, and the PDF lands in
`Documents/Scans` in their own OneDrive.

ScanRelay is one Windows service, installed like any off-the-shelf program and
run entirely from a web admin screen. It moves scans from a hidden share into
each person's OneDrive through Microsoft Graph, and keeps every printer's
address book matched to an Entra group.

- [Spec](docs/spec.pdf) — what the app does and why
- [Plan](docs/plan.md) — technical decisions, milestones, risks, open questions

## Planned layout

```
/src
  /Service      intake, upload, directory sync, module host
  /Admin        web admin screen
  /Installer
  /ModuleSdk    the adapter interface and helpers for module authors
/modules
  /<vendor>/<module>   model data and adapter code for one printer family
/catalog        the module index, generated and signed at release
/tests
  /MockDevices  fake printer endpoints so modules can be tested without hardware
/docs           install guide, per-vendor notes, how to write a module
```

Folders are created as each milestone needs them.

## Licence

[Apache License 2.0](LICENSE). Free for any use, including by businesses.
Changes and redistribution are allowed as long as the credit in
[NOTICE](NOTICE) is kept.
