# ScanRelay

> Status: **built, not yet verified on a real Windows Server or a real tenant.**
> The service, admin screen, module system and installer exist and pass their
> automated tests against a mock Microsoft 365 and a mock printer. No module
> for a real printer exists yet. See [what is and is not verified](docs/plan.md#what-is-verified).

A person picks their name on the printer panel, scans, and the PDF lands in
`Documents/Scans` in their own OneDrive.

ScanRelay is one Windows service, installed like any off-the-shelf program and
run entirely from a web admin screen. It moves scans from a hidden share into
each person's OneDrive through Microsoft Graph, and keeps every printer's
address book matched to an Entra group.

- [Install guide](docs/install.md)
- [Spec](docs/spec.pdf): what the app does and why
- [Plan](docs/plan.md): decisions, what is built, what is verified
- [Printer modules](docs/modules/README.md) and [writing a module](docs/writing-a-module.md)
- [Development](docs/development.md), [server test](docs/server-test.md), [releasing](docs/releasing.md)
- [Privacy](PRIVACY.md), [security](SECURITY.md), [contributing](CONTRIBUTING.md)

## Try it without a tenant or a printer

Needs the .NET 10 SDK.

```
dotnet run --project tests/DevHarness
```

Open `http://localhost:5080`. This runs the real app against a mock Microsoft
365 and a mock printer. [Development](docs/development.md) has the details.

## Licence

[Apache License 2.0](LICENSE). Free for any use, including by businesses.
Changes and redistribution are allowed as long as the credit in
[NOTICE](NOTICE) is kept.
