# Code signing (SignPath Foundation)

Windows releases are built and signed by `.github/workflows/windows-release.yml`. Signing goes through
[SignPath Foundation](https://signpath.org)'s free program for open-source projects: SignPath only signs
artifacts that a GitHub Actions run built from this public repository, so there is no certificate or
key to keep anywhere. Windows shows the publisher as "SignPath Foundation".

Until the repository is set up, the workflow still runs and publishes **unsigned** releases; signing
switches on by itself once the variables below exist.

## One-time setup (repository owner)

1. **Apply** at [signpath.org](https://signpath.org) for the open-source program with this repository.
   The README's "Code signing policy" section is the policy page they ask for; check it still names the
   right approvers. Their current requirements are on their site.
2. **In SignPath, once approved**:
   - install the SignPath GitHub App on this repository (it is how SignPath fetches a run's artifacts
     and checks they came from this repository's workflow);
   - create the project and a signing policy (for example `release-signing`);
   - add two artifact configurations, named exactly `app` and `installer` (below);
   - create an API token for a CI user that may submit to the policy.
3. **In GitHub → Settings → Secrets and variables → Actions**:

   | Kind | Name | Value |
   |---|---|---|
   | secret | `SIGNPATH_API_TOKEN` | the CI user's API token |
   | variable | `SIGNPATH_ORGANIZATION_ID` | the organization id (a GUID) |
   | variable | `SIGNPATH_PROJECT_SLUG` | the project's slug |
   | variable | `SIGNPATH_SIGNING_POLICY_SLUG` | the signing policy's slug |

## Artifact configurations

SignPath Foundation signs a project's **own** binaries only. The .NET runtime, NAudio, sherpa-onnx,
WPF-UI and the rest already carry their publishers' signatures, or are not ours to sign, so they pass
through unchanged.

`app`: the published app folder, uploaded by the workflow as a zip.

```xml
<?xml version="1.0" encoding="utf-8"?>
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <pe-file path="OpenClicky.exe">
      <authenticode-sign />
    </pe-file>
    <pe-file path="OpenClicky.dll">
      <authenticode-sign />
    </pe-file>
    <pe-file path="OpenClicky.Core.dll">
      <authenticode-sign />
    </pe-file>
  </zip-file>
</artifact-configuration>
```

`installer`: the Setup.exe Velopack writes after the app was signed.

```xml
<?xml version="1.0" encoding="utf-8"?>
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <pe-file path="*-Setup.exe">
      <authenticode-sign />
    </pe-file>
  </zip-file>
</artifact-configuration>
```

## Releasing

- Push a tag `vX.Y.Z`. The workflow runs the Core tests, builds x64 and ARM64, signs and packs each,
  and uploads both into the GitHub release `vX.Y.Z` (Velopack channels `win` and `win-arm64`), which is
  where the app's updater looks.
- Or run the workflow by hand (Actions → windows-release → Run workflow) with *publish* off to get the
  signed files as run artifacts without releasing anything.

With a manual-approval signing policy, each run waits in SignPath until an approver accepts it.

## What stays unsigned

Velopack's own `Update.exe` inside the packages, and the native and third-party DLLs, keep whatever
signature their publishers gave them. SmartScreen judges the installer and `OpenClicky.exe`, which are
signed. A new certificate still has to build reputation over a few releases before the "Windows
protected your PC" prompt stops appearing.

`windows/installer/build-release.ps1` stays the way to make a local, unsigned build.
