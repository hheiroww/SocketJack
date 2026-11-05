# Basic or Premium?

Choose **Basic** for the original networking/3D physics workflow when you want to wire the surrounding game integration yourself. Choose **SocketJack Premium** for the complete Unity toolset and optional MMO services, supplied together with setup tools, examples and detailed guides.

The comparison below refers to the existing basic Asset Store release and the new complete Premium distribution. It does not imply that shared public source or NuGet APIs are locked behind a paid runtime license.

| Capability | Existing Basic asset | SocketJack Premium |
| --- | --- | --- |
| Unity 6 primary support and 2022.3 compatibility | Included | Included |
| Renderer-independent networking: Built-in, URP, HDRP, Custom RP | Included | Included |
| Host/peer and dedicated server/client | Included | Included |
| UDP and TCP; reliable unordered UDP | Included | Included |
| Header-aware small frames and automatic large-value rebuilding | Included | Included, including relay envelope budgeting |
| Predicted 3D Rigidbody motion, interpolation/extrapolation and sparse repair | Included | Included |
| Selected fields/properties, per-member authority and configurable serializer | Included | Included, with precision/adaptive options |
| Existing-prefab setup wizard and stable registry/spawner helpers | Manual integration guide | Integrated, undoable setup |
| Local-player camera/audio/input helpers and Input System bridge | Manual integration | Supplied helpers |
| Real Rigidbody2D platformer and top-down synchronization | Custom integration | Supplied component and example |
| Full/Balanced/Compact/custom quantization | Not bundled in the original release | Included |
| Opt-in adaptive precision/cadence and measured diagnostics | Not bundled | Included |
| Spatial interest with grid/radius/hysteresis | Room visibility foundation | Integrated spatial tooling |
| Readiness/lifecycle, ownership transfer, scene objects/transitions and pooling | Runtime integration | Supplied Unity helpers |
| Health, doors, pickups, projectiles and Animator starting components | Game-authored | Supplied starting components |
| Native/browser WebSocket gameplay | Custom browser transport integration | Supplied transport and WebGL bridge |
| Authenticated self-hosted UDP/WebSocket relays | Separate integration | Included service/bridge modules |
| Accounts, ticketed game admission and SQLite persistence | Separate integration | Included optional modules |
| Automatically hosted native website/API | Separate integration | Included optional host |
| MMO/cooperative/arena chat presets, tabs, colors and links | Game-authored | Included |
| Persistent Communities, party/raid, invitations and moderation | Game-authored | Included |
| Linked servers with shared identities and deployment-wide Global chat | Separate integration | Included canonical hub/leaf workflow |
| Opus capture/playback, PTT, mute, jitter/PLC and authorized account voice | Separate integration | Included optional modules |
| Examples | Sliding-cube physics example | Physics Arena plus complete native 3D/2D account/chat/voice examples |
| Documentation | Basic conversion/setup guide | Detailed task guides, examples, screenshots and deployment guidance |

## What Premium does not promise

Your game still defines trusted rules, maps, prefabs and progression. Keep MMO combat, inventory and currency under server authority. Owner authority is a deliberate trust choice. Custom controllers and unusual platforms need game-specific integration.

Physics is predicted locally and repaired from canonical state; it is not deterministic lockstep or full rollback. Large deployments need measured visibility, bandwidth and simulation budgets. A 2,000-peer loopback transport test is useful evidence for connection/delivery correctness, and does not guarantee 2,000 mutually visible physics players or Internet latency.

The operator supplies reachable hosting, certificates, backups and account-data policies. Direct UDP/TCP and the authenticated UDP relay do not encrypt game payloads. Use a configured TLS/WSS path or suitable secure network when confidentiality is required. Voice is relayed by the trusted server, rather than end-to-end encrypted.

## Price and upgrade approach

SocketJack Premium is the separate **$9.99 US list-price** offering requested for the complete toolset. The existing Basic asset remains available separately; store taxes and regional checkout presentation are managed by Unity. Do not assume a draft has been approved or that an upgrade discount exists unless the live Store listing says so.

Before moving a Basic project to Premium, commit/back up the project. Retain your prefab/scene IDs, use one SDK copy, update all peers/servers together and follow the migration notes. Asset Store and UPM copies of the same SDK should not be installed side by side. The complete distribution contains the core; it does not require importing a second Basic copy.
