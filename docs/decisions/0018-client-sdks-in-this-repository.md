# 0018: Client SDKs in this repository

- Status: Accepted
- Date: 2026-11-17

## Context

V3 adds client SDKs. A client speaks Soga's HTTP API and SignalR hub, so every protocol change touches the server and the clients together. ADR 0010 versions the protocol independently of the packages, and ADR 0017 requires clients to ignore unknown fields, event types, message kinds and error codes.

## Decision

Client SDKs live in this repository, next to the server packages:

```text
src/Soga                      server (published)
src/Soga.EntityFrameworkCore  server storage (published)
src/Soga.Client               .NET client for Blazor, MAUI, desktop and console apps
```

`Soga.Client` is a plain .NET library on the official SignalR .NET client, which already runs in Blazor WebAssembly, so one client serves every .NET app. Blazor UI components, if added, build on it in a separate package.

All packages build and release together from one tag with one package version. The protocol version stays separate (ADR 0010).

`Soga.Client` starts as a placeholder with `IsPackable` set to false. It becomes packable only once it is implemented and tested against the real server.

## Alternatives considered

A separate client repository was rejected: a protocol change would need coordinated pull requests and releases in two places, and the client could not be tested against the server in the same build.

A Blazor-only client was rejected: the SignalR .NET client works everywhere .NET runs, so a Blazor-only package would leave MAUI, desktop and console apps without a client for no technical reason.

## Consequences

Integration tests can run the client against the real server host in one build. Client and server releases share a version number, so a client release may contain no client changes and the reverse.

## Security and migration implications

None. The client sends credentials the host application obtains; it never handles server-side secrets.
