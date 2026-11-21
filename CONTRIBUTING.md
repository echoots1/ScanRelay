# Contributing

The project does not take outside code submissions. Every module in this repo
is written and tested by the maintainers.

To support another printer, fork the repo, build a module there with the
module SDK, and install it as a custom module. The licence allows this as long
as the credit is kept.

Bug reports are welcome as issues. Security problems go through
[SECURITY.md](SECURITY.md) instead.

## Rules for the repo

- No tenant IDs, host names, addresses or people's names in code, tests or
  samples.
- No vendor software is included. Tools such as Brother's Mass Deployment Tool
  are downloaded by the user.
- Methods a vendor does not document are labelled unofficial in the module and
  in the docs.
- Vendor and product names are used only to say what is supported.
- Every change builds, runs unit tests, runs module tests against the mock
  devices, and checks module data against a schema.
