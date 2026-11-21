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
4. Commit, then tag and push: `git tag v1.2.0 && git push origin v1.2.0`.
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
