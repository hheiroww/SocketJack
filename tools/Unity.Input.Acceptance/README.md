# Input bridge acceptance

`Build-And-Test.ps1` creates one fresh owned project outside source, copies the public base component allowlist and actual distribution asmdefs, and runs a real Editor Play Mode host. The base component assembly intentionally has no Input System reference.

Run once without `-InputSystemPackage` for the legacy/package-absent case. Run a separate case with an explicitly supplied official `com.unity.inputsystem` package directory for Input System-only mode. The script changes settings only inside its new test project; it never installs a package or changes input settings in a user's project. Supply a matching release `-RuntimeDll` and optionally its `-ExpectedRuntimeSha256`.

The present-package case uses synthetic Input System keyboard events, not the user's keyboard. It checks default WASD/arrows/Space on real ready 3D/2D owners, no remote input, assigned action clones, clone/source isolation, disabled/suppressed/ownership teardown, owner return and clearing references. The absent case checks compilation and a neutral legacy branch. Receipts and logs remain in the owned output. No process killing or deletion is performed. This Editor check does not establish console/device behavior or IL2CPP stripping acceptance by itself.
