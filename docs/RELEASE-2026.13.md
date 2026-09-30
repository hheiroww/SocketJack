# SocketJack 2026.13.0 release checks

## Changes

- Refreshed GitHub and NuGet READMEs with matching feature descriptions, symbols, valid fenced examples, and expandable sections.
- Documents reliable UDP, the custom SB binary serializer, verified transfers, TypeScript client generation, and SSH.NET 2026.0.0.
- Moves JackLLM application pages, icons, and visual-editor resource links out of SocketJack.dll into the existing JackLLM extension project, SocketJack.LlmCore. Its internal page loader reads the application assembly. Core HTTP and SQL administration pages remain in SocketJack.
- Moves application-specific tests to heirowLLM.Core.Tests; core tests no longer reference the application extension.
- Enables direct/transitive NuGet auditing. NU1900 through NU1905 fail restore, covering reported vulnerabilities and audit-source failures.

## Security audit — September 30, 2026

The core package and the SocketJack projects included in SocketJack.sln reported no known vulnerable direct or transitive packages from the configured NuGet feeds. SSH.NET resolves to 2026.0.0.

The solution also references an **external checkout**, `wShare/OnlineUsers/OnlineUsers.vbproj`. That project reported Azure.Identity 1.7.0 and Microsoft.IdentityModel.JsonWebTokens / System.IdentityModel.Tokens.Jwt 6.24.0 advisories. These are not SocketJack package dependencies and this release does not alter that external repository. The wider solution scan must not be described as entirely clean.

An audit is a check against known advisories at that time, not a guarantee of complete security.

## Validation

- Release build of core and JackLLM extension: passed.
- Reliable UDP suite: 30 passed.
- Core resource boundary: passed; only the four library-owned HTML pages are embedded.
- Application resource test: passed; chat HTML, visual-editor HTML, and favicon load from the application assembly.
- Initial broader suite: two broadcast timing failures and four existing application-test failures (authentication assumptions / missing desktop source). Application tests are now separated from the core test project. A focused rerun passed the excluded-client case but the cross-protocol broadcast case remained intermittent. These failures are not claimed fixed by this release.

## Compatibility

Consumers must use the JackLLM application assembly for its application pages; those pages are no longer exported as SocketJack core resources. The core package has no project reference to JackLLM or the visual editor. The separately versioned WPF package is not republished by this release.

Reliable UDP does not provide encryption and its recorded loopback benchmarks did not meet the stated TCP performance target. See [results](UDP_Reliable-results.md).
