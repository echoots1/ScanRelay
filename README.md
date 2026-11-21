# ScanRelay

> Status: **early development.** The foundations are in place. It does not move scans yet.

A person picks their name on the printer panel, scans, and the PDF lands in
`Documents/Scans` in their own OneDrive.

ScanRelay is one Windows service, installed like any off-the-shelf program and
run entirely from a web admin screen. It moves scans from a hidden share into
each person's OneDrive through Microsoft Graph, and keeps every printer's
address book matched to an Entra group.

- [Spec](docs/spec.pdf) — what the app does and why
- [Plan](docs/plan.md) — technical decisions, milestones, risks, open questions

## Layout

```
/src
  /Core         database and shared types
  /Service      the Windows service: intake, upload, directory sync, module host
  /Admin        web admin screen, served by the service
  /ModuleSdk    the adapter interface and helpers for module authors
  /Installer    (planned)
/modules        (planned) model data and adapter code, one folder per printer family
/catalog        (planned) the module index, generated and signed at release
/tests
/docs
```

## Building

Needs the .NET 10 SDK.

```
dotnet build ScanRelay.slnx
dotnet test ScanRelay.slnx
```

To run the service on a development machine, with its data in a folder of your choice:

```
dotnet run --project src/Service -- --DataFolder=./data --urls=http://localhost:5080
```

Then open `http://localhost:5080/health`.

After changing the database model, add a migration:

```
dotnet tool restore
dotnet ef migrations add <Name> --project src/Core -o Data/Migrations
```

## Licence

[Apache License 2.0](LICENSE). Free for any use, including by businesses.
Changes and redistribution are allowed as long as the credit in
[NOTICE](NOTICE) is kept.
