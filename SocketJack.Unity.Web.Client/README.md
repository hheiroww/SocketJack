# SocketJack.Unity.Web.Client

Optional accounts, MMO text chat and social UI for SocketJack.Unity. Unity 6 is the primary bracket; Unity 2022.3 is the compatibility bracket. The portable library targets .NET Standard 2.1.

The Asset Store distribution includes the matching DLLs and editable Unity scripts. Ordinary .NET consumers install this package through NuGet. Manual Unity/NuGet import also requires the matching SocketJack.Unity runtime and the package's System.Text.Json dependencies. Copy component sources from `contentFiles/any/any/SocketJack.Unity.Web.Client/`; keep `SocketJackWebApi.jslib` in a Unity `Plugins` directory for browser builds.

Add `SocketJackWebClientBehaviour`, `SocketJackMMOChatPanel` and `SocketJackSocialPanel` to one scene object. Configure the operator-owned backend URL. A `SocketJackChatPreset` customizes channel labels/colors, tabs and panel settings. MMO, Cooperative, Arena and Custom presets are provided. Communities replace guild terminology. Individual fields/properties on your game objects still use the base `SocketJackMemberSync` component.

The supplied UI signs in/registers accounts and supports global/trade/zone/proximity, party/raid, Community/officer and whisper channels. Membership, moderation and proximity positions are checked by the server. Public game links are registered in the trusted server catalog; render their returned metadata rather than evaluating message text as HTML or code.

`SocketJackWebClient` provides portable asynchronous account, chat/history, social and WebSocket API methods. Tokens remain in memory and are never placed in URLs. Native .NET uses HttpClient/ClientWebSocket; Unity browser builds inject UnityWebRequest and the supplied JavaScript WebSocket bridge. Enable Unity's built-in UnityWebRequest module for browser clients. HTTPS/WSS is required outside loopback development.

`JoinGameAsync(manager, serverId)` obtains a short-lived one-use ticket and joins the selected game server. An authenticated local host uses `NetworkManager.StartHostListener()`, signs in once its website is ready, then calls `ConnectLocalHostAsync(manager, serverId)`. Use the same prefab registry, member schemas and serializer configuration in all participants.

This client does not contain SQLite or host a website. Use SocketJack.Unity.Web for native server hosting. Source, issues and setup guides: https://github.com/hheiroww/SocketJack
