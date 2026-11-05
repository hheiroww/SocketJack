# Actual Unity UI capture

`Capture.ps1` reuses an existing task-owned Physics2D acceptance project and player. It builds a new scene containing the unchanged account-voice acceptance fixture and the screenshot helper, then runs the player hidden with graphics enabled. It does not send OS input or bring a window to the foreground.

The fixture uses local synthetic accounts, a canonical hub and an enrolled leaf, real API chat messages and registered links, trusted Combat messages, and synthetic Opus audio. It never opens a physical microphone. Only the actual customer `SocketJackMMOChatPanel`, `SocketJackSocialPanel` and `SocketJackVoiceControls` appear in the screenshots. A plain camera backdrop is intentional; no gameplay world or screenshot composite is fabricated.

```powershell
./tools/Unity.UI.Acceptance/Capture.ps1 `
  -UnityEditor $editor `
  -FixtureDirectory $ownedPhysics2dFixture `
  -ModuleDirectory $verifiedModuleDirectory `
  -OutputDirectory $privateCaptureStaging `
  -ExpectedRuntimeSha256 $reviewedRuntimeHash
```

The module directory must contain the explicit twelve-DLL distribution. Unity's framework `System.Numerics.Vectors` copy is excluded. The script checks project ownership and idle processes, preserves prior receipts, records input hashes and rebuild impact, and retains logs, PNG hashes and both UI/voice acceptance receipts. The reused player now starts the capture scene; rebuild the original Physics2D/Voice fixture to restore its former entry scene.

All generated account databases, receipts and logs stay in private staging. Inspect every raw PNG for readability and private data before copying only reviewed images into public documentation. No cleanup deletion or process termination is performed.
