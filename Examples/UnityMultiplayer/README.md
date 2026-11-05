# Complete account-backed multiplayer sample

Open `Complete3D` or `Complete2D` from the Asset Store sample folder. For a UPM installation, import Complete Multiplayer from the package Samples tab and create a scene through **Tools → SocketJack → Samples**. These native samples require the complete optional Web and Voice distribution. Windows and Linux server prerequisites are in `ReleaseTwo.md`.

Run one matching build as a host listener or dedicated server. Its account website starts at `http://127.0.0.1:7778` for local development. Create/sign in to an account through the lower-left chat panel, then select **Join authenticated game**. A host signs in after starting its listener. A second player enters the host address and the same website origin in the chat panel, signs in with a different account and joins. Choose the same UDP/TCP option in both players.

Move with WASD/arrows and jump with Space. Each player predicts its own physics; sparse canonical repair and visual interpolation synchronize the square/cube. Health demonstrates server-owned member replication. The manager includes pooling, spatial visibility and opt-in adaptive precision. The demo's player spawner supplies trusted proximity positions.

Use the MMO tabs and Social panel for Global/Trade/Party/Raid/Community chat and membership. Voice uses the authenticated account connection; enable the microphone explicitly, select an authorized channel and hold V. Native game/audio acceptance uses synthetic PCM and does not record the developer's microphone. Test actual devices and permissions separately.

The scene creates demo-owned objects at runtime. It does not edit project input/rendering settings, open router mappings or store credentials in assets. For a custom render pipeline, assign a compatible material to `MultiplayerGame.MaterialTemplate` and retain its shader in the player build. This demonstration uses a single static level; scene transition/ownership/Animator acceptance fixtures live in the source repository's tools, outside the distributed asset.

Account state is stored outside Assets in the native player's persistent-data directory. Use separate node IDs/data directories for independent deployments. Public hosting needs the certificate/origin/network configuration described in `ReleaseTwo.md`; loopback HTTP is not a production deployment.
