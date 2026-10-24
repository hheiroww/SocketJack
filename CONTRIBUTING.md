Pull requests are welcome. For major changes, please open an issue first

to discuss what you would like to change.


## Release versions

Before every release push, update `SocketJack/SocketJack.csproj`, `SocketJack.Windows/SocketJack.WPF.csproj`, and their README version references to `yyyy.MM.dd.revision`, using the release date in America/Chicago. Begin each date at revision `0`; increment the revision for another release on the same date. Each published version is immutable and must be unused on NuGet.

Run `pwsh -File tools/Verify-ReleaseVersion.ps1` and `pwsh -File tools/Verify-ReleaseVersion.ps1 -Project SocketJack.Windows/SocketJack.WPF.csproj -PackageId SocketJack.WPF` before packaging. The publishing workflow runs both checks and fails when a version has the wrong date or is already published. Confirm both packages are downloadable from NuGet after the workflow finishes; a successful build alone does not establish publication.

Both libraries use the same release version. After packing, run `pwsh -File tools/Get-ReleasePackages.ps1` to verify the two-package allowlist, package identities, repository links, and package contents. The workflow publishes these exact packages to NuGet and GitHub Packages, then creates the matching `v<version>` GitHub Release at the built commit with both `.nupkg` assets and `SHA256SUMS`. Confirm the release assets and both registry versions after publication.
