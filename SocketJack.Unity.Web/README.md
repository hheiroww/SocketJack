# SocketJack.Unity.Web

Optional operator-owned accounts, Communities, chat, website, diagnostics and authenticated relay hosting for SocketJack.Unity. This module targets .NET Standard 2.1; it is separate from the dependency-free base networking DLL. It depends on the matching Web.Client package and MIT-licensed System.Text.Json libraries. No NAudio, SQLitePCL, hosted subscription or SocketJack.com account is required.

## Unity server setup

Add `SocketJackWebHost` to the `NetworkManager` scene object. A supported native listening host/server starts the website automatically. Ordinary game clients do not open a listener or create an account database. The Inspector shows its URL and, locally, the one-time first-operator setup code. Connect UI buttons through the supplied Web.Client components.

The default website binds to `127.0.0.1:7778` for development. A network-facing website requires a server certificate and an explicit bind address; put the PFX and its password outside game assets, with the password supplied by the named environment variable. Configure the public UDP relay hostname when listening on a wildcard address. The optional UDP relay defaults to the next port after the website; WSS relay uses `/ws/relay`.

Account data defaults to `Application.persistentDataPath/SocketJack/<nodeId>/accounts.sqlite`, outside the public website. The supplied store uses the operating-system SQLite C API: Windows 10/11 `winsqlite3.dll`, or Linux `libsqlite3.so.0` version 3.24 or newer (`libsqlite3-0` on Debian/Ubuntu). No SQLite native binary or managed wrapper is bundled. Other native server platforms can supply `IAccountStore`/`ISocialStore` adapters. Browser builds use Web.Client and cannot run this native listener/store.

Password verification uses salted PBKDF2-SHA256 with 600,000 iterations and bounded background workers. Sessions are opaque, hashed in storage, expiring and revokable. Operator setup is loopback-only; player recovery can be performed by an authorized operator through `accounts/reset-password`. Never replicate credentials through gameplay member synchronization.

## Linked servers

One canonical hub owns accounts and persistent social state. An operator calls `nodes/issue-link` to create a one-use ten-minute enrollment token. Configure each leaf's unique node ID, hub HTTPS origin and enrollment-token environment variable. The leaf persists its returned node credential in its private database; the hub stores only its hash. `nodes/revoke` disables it.

Leaf websites use a bounded bridge to their fixed configured hub. Global chat and Communities therefore span rooms and linked servers. Main-thread game presence snapshots support zone/proximity routing; untrusted web clients cannot post positions. The hub confirms game-ticket installation at the target leaf before returning admission data. With a custom player spawner, set `SocketJackWebHost.PresenceObjectResolver` to the authoritative player identity; the built-in player spawner is recognized automatically.

## API and customization

HTTP routes use `/api/v1/`; authenticated realtime operations use `/ws/api` with `{id,op,data}` requests and `{id,ok,data,error}` replies. Authentication is an HTTPS request or first WSS login/resume operation, never a query-string credential. Separate optional binary roles can register authenticated `/ws/name` handlers, including voice.

The bundled sharp monochrome website includes login/profile, colored MMO chat, Communities, groups, server status and operator diagnostics. Editable HTML/CSS/JavaScript sources ship with the package; no CDN/build service is needed. The native default website is embedded in the DLL for reliable standalone delivery.

`relay/create`, `relay/join`, `relay/renew` and `relay/close` issue account-bound WSS tickets or UDP allocations. WSS tickets are consumed once; UDP allocations can renew without changing their routing ID/key. Relays forward only allocated game sessions and preserve normal game admission checks. Direct UDP stays the default game transport; WSS retains ordered-stream latency behavior. Configure a real reachable relay endpoint and measure your Internet path.

The diagnostics API is operator-only. Transport counters exclude lower-level IP/TCP/UDP/TLS framing as documented by the base metrics API. WebSocket connectivity does not prove UDP reachability or production world capacity.

Source and complete setup guides: https://github.com/hheiroww/SocketJack
