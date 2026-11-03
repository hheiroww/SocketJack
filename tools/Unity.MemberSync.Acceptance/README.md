# Unity member synchronization acceptance

This fixture runs real `NetworkManager` and `SocketJackMemberSync` components in Editor Play Mode and a native player. It checks serialized Inspector provider references, `AddMember` prefab clone targets, opt-in fields/properties, server and owner authority, Unity vector codecs, disabled unsupported references, large member fragmentation, observers and late joins. Six cases cover UDP/TCP, the default SocketJack formatter, a custom serializer provider, and Host / Peer shared scene instances.

Run from PowerShell with a licensed installed Unity Editor and the .NET SDK:

```powershell
.\tools\Unity.MemberSync.Acceptance\Build-And-Test.ps1 `
  -UnityEditor '<editor installation>/Editor/Unity.exe' `
  -UnityVersion '6000.3.27f2' `
  -OutputDirectory '<owned test output directory>'
```

Use a distinct output directory for each Unity version. `-RuntimeDll` can reuse an already verified current build. The script copies an explicit source allowlist into its isolated project, builds a test player, and writes JSON receipts and logs. Output files are retained. It never starts router mapping. These probe scripts are test tooling and are excluded from the NuGet and Asset Store packages. The isolated test player also retains the ordinary physics sample route when `-member-sync-probe` is absent; it is not a release player.

The Unity 6 Editor fixture marks background Search startup indexing complete for that test session before entering Play Mode. This avoids an unrelated batch Editor indexing race across the test's domain reload; it does not change Editor preferences or networking behavior. All runtime error logs still fail the probe.

The local loopback checks validate component wiring and serialization behavior. They do not measure WAN capacity or production server throughput.
