# Publishing Axiom updates

Axiom 1.7.0 and newer can update itself from stable releases in
[`YoMosa2009/Axiom`](https://github.com/YoMosa2009/Axiom/releases).

## Publish a release or patch

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
