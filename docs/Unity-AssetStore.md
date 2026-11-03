# Unity Asset Store artifact preparation

`tools/Export-UnityAssetStore.ps1` creates a real Unity `.unitypackage`, then imports it into a separate clean project and verifies that the runtime, component scripts and demo scene compile and load. It uses the installed Unity Editor's `AssetDatabase.ExportPackage` and an explicit asset allowlist. It does not upload or submit a listing.

```powershell
pwsh -File tools/Export-UnityAssetStore.ps1 -OutputDirectory <owned-staging-directory>
```

Use an activated Unity 2022.3.17f1 Editor with Windows Standalone support. The output directory holds one export project, one fresh-import project, the package, manifests, hashes and Editor logs. Dependencies are reused from the installed Editor/global NuGet cache; no existing `Library` folder or player/runtime installation is copied. Generated files are retained for review; this script performs no cleanup deletion.

The package root is `Assets/SocketJack.Unity`. The allowlist includes the managed runtime DLL, editable Unity component/Inspector sources, linker configuration, physics sample/build helper, generated demo scene/material, package icon, offline documentation and license. Project settings, packages caches, logs, credentials, customer data and application code are outside the package.

To reuse a runtime DLL that has already passed native-player checks, provide `-RuntimeDll <verified-dll> -ExpectedRuntimeSha256 <sha256>`. The exporter checks assembly identity, version and hash, then preserves those exact bytes. Use `-ImportVerificationTag <label>` for a separately named clean import project when comparing artifacts; an existing imported project is never presented as a fresh import.

Public runtime exports exclude machine/task debug paths. Source builds disable debug-symbol generation, and the exporter rejects explicit DLLs containing absolute CodeView paths or unaudited embedded PDBs. Revalidate the clean DLL in the intended player before using its hash as native acceptance evidence.

`-DocumentationOnly` permits a final offline-documentation update after a successful import. It requires the same explicit runtime, verifies every non-documentation source/staged asset against the earlier archive checkpoint, preserves the demo scene/material bytes, and records the new archive hash separately from the original imported-code hash. Any code or sample change requires a new full import verification.

Archive verification reads the gzip/tar structure, validates Unity GUID records and canonical asset paths, rejects unlisted entries and compares packaged payload hashes to the staged source assets. Fresh import is a separate real Editor launch so imported C# scripts compile before acceptance is recorded. These checks establish an importable artifact; they do not establish Unity Asset Store approval or acceptance on every player platform.

`Unity-AssetStore-QuickStart.md` is the offline customer quick start. `Unity-AssetStore-listing-draft.md` contains suggested listing text, compatibility boundaries, AI-assistance disclosure and a pricing field to complete before submission.

The shipped API uses `SocketJack.Multiplayer` and `SocketJack.Multiplayer.Components`; product/assembly paths retain `SocketJack.Unity`. Sample code has its own `SocketJack.Samples.Physics` namespaces and editor actions live under **Tools → SocketJack**. `Unity-Third-Party-Notices.txt` records the first-party MIT code and Unity-provided dependencies without bundling engine or publishing-tool binaries.

Before any external submission, review the exact artifact and listing in the intended publisher account, run the official publishing validator, resolve any pricing/metadata/license requirements and submit through Unity's normal review process. The exporter neither changes publisher settings nor accepts agreements.

Official references: [Asset Store submission guidelines](https://assetstore.unity.com/publishing/submission-guidelines), [publishing validation and upload](https://docs.unity.com/en-us/asset-store/publishing/asset-packages/upload), [Unity 2022.3 ExportPackage](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.ExportPackage.html).
