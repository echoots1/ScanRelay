# Releasing

Every release is built by the pipeline in `.github/workflows/release.yml`,
from the public repo. Nothing is built on a personal computer.

## One-time setup

1. **Make the module signing key.** On any machine with the .NET SDK:

   ```
   dotnet run --project tools/ModuleTool -- keygen private.pem catalog/signing-key.pub.pem
   ```

2. **Store the private half as a secret.** In the repo's settings, create an
   environment called `release`. Add a secret to it named
   `MODULE_SIGNING_KEY` holding the contents of `private.pem`. Then delete
   `private.pem`. It must not stay on a personal computer and must never be
   committed.

3. **Commit the public half,** `catalog/signing-key.pub.pem`. The app and the
   installer are built with it and trust only modules and module lists signed
   by the matching private key.

4. **Name who approves releases.** On the `release` environment, add the
   maintainer under "Required reviewers". The pipeline then stops before
   building until they approve.

5. **Require multi-factor sign-in** for everyone with write access to the repo.

Until steps 1 to 3 are done, no official module can be installed. Custom
modules still can.

## Each release

1. Set `<Version>` in `Directory.Build.props`.
2. Write `docs/releases/<version>.md`. This becomes the release text, and the
   app copies it into the email it sends to admins, so write it for them:
   - A short list of what changed.
   - `[security]` in the first line if it fixes a security problem. The email
     subject then says so.
   - Any step beyond the normal update. The defined cases are in the spec
     under "When a release needs more": a new Graph permission, an installed
     module with no version for the new app, a version too old to update
     directly, and a setting that changed meaning.
3. Run the server test in [server-test.md](server-test.md).
4. Commit and push. Then on GitHub open Actions, Release, and press **Run workflow** on the branch to release.
5. Approve the run when asked.
6. The pipeline leaves a **draft** release with the installer, each module
   package and its signature, the signed module list, and `SHA256SUMS.txt`.
   Read it over and publish it.

Servers with the update check on will notice within a day.

## What is not done yet

The installer is not signed, so Windows shows an unknown publisher warning.
The project is run so that it can apply for free open-source signing once it
has a release history. Left until then: the code signing policy page, the
credit line the signing programme requires, and connecting this pipeline to
the signing service.

## Publishing a module between releases

A new or updated printer module does not need a new version of ScanRelay.
`.github/workflows/modules.yml` ("Publish modules" on the Actions page)
builds every module, signs the ones that are new or have a higher version,
and adds them to the **latest published release**, replacing that release's
module list. The app and the installer read the module list from the latest
release, so every install sees the new module at its next check, with nothing
to reinstall: it is added in the installer's Modules step, or with the
"Add or update modules" button on the Modules page.

1. Raise `version` in the module's `module.yaml`. A module already published
   at the same version is never replaced, so a change without a new version is
   not published.
2. Merge it to the branch releases are made from.
3. Run "Publish modules" from the Actions page on that branch and approve it.

A module published this way must load in apps older than the code it was built
beside. That is why `ScanRelay.ModuleSdk` has one fixed assembly version that
never changes: a module records the version it was built against, and apps
before 0.2.8 refuse a module that asks for a newer copy than they have. The
module tool refuses to pack a module that would not load in the oldest app it
names.

Both pipelines build and pack the modules with `build/pack-modules.ps1`, and CI runs it on every change.

What it will not do:

- **Publish a module that needs a newer app than the latest release.** The
  module list holds one version of each module, so that would take the module
  away from everyone on the current release. Such a module goes out with the
  app release it needs.
- **Lower a module's version.**
- **Publish anything unsigned.** Both halves of the signing key must be in
  place, and it stops if they do not match.

The release page gets a "Modules published since this release" section naming
what was added and when, and its `SHA256SUMS.txt` is brought up to date. The
installer on that release is not touched.

A later release of the app builds every module again from the same branch, so
it carries the same module versions.

