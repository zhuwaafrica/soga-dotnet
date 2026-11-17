# Soga

<p align="center">
  <img src="assets/soga-package-icon.png" alt="Soga logo" width="180">
</p>

> Build conversations. Own your infrastructure.

Soga is an open-source, self-hosted messaging framework for ASP.NET Core. It adds the application layer you need above SignalR: direct and group conversations, persistent messages, roles, authorization, history, editing and deleting, read state, realtime events, and recovery after a disconnect.

Soga runs inside your own ASP.NET Core application. It is not a hosted service: you keep control of your users, database, messages, deployment and infrastructure.

**Contents:** [Why Soga?](#why-soga) · [Features](#features) · [Requirements](#requirements) · [Installation](#installation) · [Usage](#usage) · [Packages](#packages) · [Samples](#samples) · [Documentation](#documentation) · [Contributing](#contributing) · [Security](#security) · [License](#license)

## Why Soga?

SignalR gives you realtime communication, but not a messaging domain. Teams still have to build message persistence, conversation membership, roles, permissions, duplicate-send protection, history, unread state, and recovery after a disconnect. Soga packages all of that into one ASP.NET Core framework.

## Features

- Your application's authentication, with optional tenancy.
- Direct conversations, and group conversations with owner, admin and member roles.
- Durable text messages: safe to retry, editable, soft-deletable, with replies and system messages.
- A fixed order per conversation and cursor-based history.
- Realtime delivery through SignalR from a transactional outbox, with typing indicators and single-server presence.
- Reconnection that recovers every change a user missed, including membership changes.
- Read positions, unread counts, and conversation lists with filters and last-message previews.
- Extension points for your own authorization policy, message validator, audit sink, event subscribers and system messages.
- EF Core storage for SQL Server and PostgreSQL (SQLite for development), plus health checks, logging, tracing and metrics.

Attachments, reactions, threads, notifications, client SDKs and distributed presence come later.

## Requirements

- .NET 10 or later.
- An ASP.NET Core application that already signs users in. Soga uses your users and never creates accounts.
- For production: SQL Server or PostgreSQL through EF Core. SQLite and the in-memory provider are for development only.

## Installation

```bash
dotnet add package Soga
dotnet add package Soga.EntityFrameworkCore
```

## Usage

Register Soga and map its endpoints in `Program.cs`:

```csharp
builder.Services
    .AddSoga(options =>
    {
        options.RoutePrefix = "/soga/v1";
        options.Groups.MaximumParticipants = 256;
        options.Messages.MaximumTextLength = 8_000;
    })
    .UseEntityFrameworkCore<AppDbContext>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapSoga();   // HTTP API and SignalR hub, under the route prefix
```

Add Soga's tables to your `DbContext`, then create a migration as usual:

```csharp
// SQL Server needs a case-sensitive collation for identity columns; see the deployment guide.
protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddSoga();
```

`MapSoga()` maps Soga's HTTP API and SignalR hub inside your own application; `/soga/v1` is a configurable prefix, not an external service. Your application owns the database migrations.

Clients send commands over HTTP and receive live events from the hub. [Getting started](docs/getting-started/README.md) walks through sending a message, groups, realtime events, recovering after a disconnect, and adding your own rules.

## Packages

| Package | Purpose |
| --- | --- |
| `Soga` | The messaging framework: HTTP API, SignalR hub, protocol, extension points, and an in-memory provider for development |
| `Soga.EntityFrameworkCore` | Database storage through EF Core |

A .NET client (`Soga.Client`, for Blazor, MAUI, desktop and console apps) is planned in this repository; see [ADR 0018](docs/decisions/0018-client-sdks-in-this-repository.md).

## Samples

```bash
dotnet run --project samples/Soga.MinimalSample        # in-memory, the smallest setup
dotnet run --project samples/Soga.TeamChatSample       # groups, roles, audit, system messages, host rules
dotnet run --project samples/Soga.MarketplaceSample    # EF Core + SQLite, one conversation per order
```

The samples trust an `X-Soga-User` header to stay short. Never do that in production.

## Documentation

- [Getting started](docs/getting-started/README.md): install, configure, groups, edit and delete, realtime, sync, your own rules
- [Concepts](docs/concepts/README.md): guarantees, roles, messages, sync, events, the HTTP API and error codes
- [How Soga works](docs/architecture.md): diagrams of requests, sending, retries, live delivery, reconnection, groups and tables
- [Security](docs/security/README.md): trust model, threat model, known limits
- [Deployment](docs/deployment/README.md): production checklist, databases, configuration, operations
- [V1 to V2 migration](docs/migration/v1-to-v2.md)
- [Architecture decisions](docs/decisions/) and the [architecture and engineering guide](SOGA_ARCHITECTURE_AND_ENGINEERING_GUIDE.md)

## Contributing

```bash
dotnet build -warnaserror
dotnet test
```

The SQL Server and PostgreSQL test suites run only when `SOGA_TEST_SQLSERVER` or `SOGA_TEST_POSTGRES` point at a server that may create databases. Read [CONTRIBUTING.md](CONTRIBUTING.md) before you start; discuss major architecture or public API changes first and record them as architecture decisions.

## Security

Soga requires authentication by default and never trusts a sender identity sent by a client. It relies on HTTPS for transport security and does not provide end-to-end encryption. Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## License

Soga is created and maintained by Zhuwa Africa Limited and licensed under the [Apache License 2.0](LICENSE).
