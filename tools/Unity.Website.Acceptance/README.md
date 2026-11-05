# Isolated website UI acceptance

Start the optional backend's loopback-only `SocketJack.Unity.Web.Tests --serve` mode in an owned staging directory outside source, then run:

```text
node probe-website.mjs <installed-chromium-or-headless-shell> http://127.0.0.1:<port> <fresh-owned-output>
```

Node's built-in WebSocket API drives a separate browser profile. The probe creates a synthetic account with a random password held only in process memory, submits real UI forms, sends chat, creates a Community and party, checks status/mobile layout and signs out. It captures PNGs and a redacted receipt. It never touches a person's browser profile, records a microphone, changes an external publisher listing or prints account credentials. The owned browser is closed through its protocol. Stop the separately owned loopback server after the run; no output deletion is performed.

Keep all profiles, screenshots, accounts/databases and receipts outside source control. These test tools are excluded from the asset and NuGet content.
