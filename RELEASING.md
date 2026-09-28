# Publishing Axiom updates

Axiom 1.7.0 and newer can update itself from stable releases in
[`YoMosa2009/Axiom`](https://github.com/YoMosa2009/Axiom/releases).

Choose and synchronize the release number using [VERSIONING.md](VERSIONING.md)
before building a package.

## Automatic cloud release (default)

`.github/workflows/release.yml` publishes updates without a local machine:

1. On the change's branch, bump the version per [VERSIONING.md](VERSIONING.md)
   (`Malx_AI.csproj`, `MainWindow.xaml` label, `README.md` badge) and add the dated
   `CHANGELOG.md` entry.
2. Open a pull request. `.github/workflows/ci.yml` runs the version check, unit tests,
   and a Release build on a Windows runner.
3. Merge to `main`. If no GitHub Release exists for the new version, the workflow runs
   `scripts/Publish-GitHubRelease.ps1` on a Windows runner, which tests, packages,
   tags, and publishes the release. Installed copies receive it as an in-app update.

Merges that do not change `<Version>` publish nothing. The built ZIP and notes are also
kept as a workflow artifact for 14 days.

The release job fails rather than publishing if any embedded OAuth app value is missing.
Add these repository secrets (Settings → Secrets and variables → Actions) with the same
values as the local `%LOCALAPPDATA%\Axiom\SharedOAuth` files:

- `AXIOM_BUILTIN_GOOGLE_CLIENT_ID`, `AXIOM_BUILTIN_GOOGLE_CLIENT_SECRET`
- `AXIOM_BUILTIN_GITHUB_CLIENT_ID`
- `AXIOM_BUILTIN_TODOIST_CLIENT_ID`, `AXIOM_BUILTIN_TODOIST_CLIENT_SECRET`

## Publish a release or patch locally


The normal release workflow is one command from a clean, synchronized `main` branch:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-GitHubRelease.ps1
```

It reads the project version, runs tests, creates the clean package under
`E:\Axiom-Updates`, extracts the matching `CHANGELOG.md` section into release notes,
creates the GitHub tag and release, and uploads the ZIP.

Pass `-OutputRoot <path>` when the default drive is not available. The script does not
touch `AXIOM_UPDATE_DIR`; add `-SetUpdateDir` only if you want this Windows account's
in-app updater pointed at the build output, and remember that the variable persists —
if that drive later fills up or is disconnected, in-app updates will fail until it is
changed back.

Use `-PackageOnly` to generate and test the local ZIP and notes without publishing.

### Manual fallback

1. Update `<Version>` in `Malx_AI/Malx_AI.csproj`, for example `1.7.1`.
2. Build the clean Windows package:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\Publish-CleanRelease.ps1
   ```

3. Test the generated folder under `artifacts/Axiom-Release`.
4. On GitHub, create a non-draft, non-prerelease release whose tag contains the
   same version, for example `v1.7.1`.
5. Upload the generated `Axiom-v1.7.1-win-x64-clean.zip` asset without changing
   its contents or filename, add release notes, and publish the release.

The publish script adds `AXIOM_UPDATE_MANIFEST.txt`. Axiom rejects ZIPs that do
not contain this manifest, contain unsafe paths, contain files missing from the
manifest, or whose packaged executable version differs from the release tag.
When GitHub supplies the asset SHA-256 digest, Axiom verifies that digest before
extracting the ZIP.

User data is not stored in the install folder. Chats, settings, connectors,
local models, and Workplace state remain under `%LOCALAPPDATA%\Axiom` while the
package-managed application files are replaced.
