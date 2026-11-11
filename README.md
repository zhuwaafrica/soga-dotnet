# Soga

<p align="center">
  <img src="assets/soga-package-icon.png" alt="Soga logo" width="180">
</p>

> Build conversations. Own your infrastructure.

Soga is an open-source, self-hosted messaging framework for ASP.NET Core. It provides the application layer developers need above SignalR, including direct and group conversations, persistent messages, roles, authorization, message history, editing and deleting, read state, realtime events, and reconnection support.

Soga runs inside the developer's own ASP.NET Core application. It is not a hosted SaaS service. The developer keeps control of their users, database, messages, deployment, and infrastructure.

> **Status:** Soga is in preview (`0.2.0-preview.1`). The direct-messaging and group-messaging backends are implemented and tested, but the public API, protocol, and database schema are not stable and it is not yet recommended for production use. Client SDKs and multi-server scale-out are planned for V3.

## Why Soga?

SignalR provides realtime communication, but it does not provide a complete messaging domain. Teams still need to implement message persistence, conversation membership, roles, permissions, duplicate-send protection, history, unread state, and recovery after disconnection.

Soga packages these capabilities into a reusable ASP.NET Core framework.

## What it does

```mermaid
flowchart LR
    A[Sign in with your app] --> B[Open a direct chat<br/>or create a group]
    B --> C[Send a message<br/>safe to retry]
    C --> D[Stored first]
    D --> E[Pushed live<br/>to every device]
    E --> F[Edit, delete,<br/>or reply]
    F --> G[Go offline]
    G --> H[Reconnect]
    H --> I[Recover everything missed<br/>that you may still see]
    I --> J[Mark as read]
```

- integration with the host application's authentication and optional tenancy;
- direct conversations and group conversations with owner, admin, and member roles;
- durable text messages with safe retry (idempotency), editing, soft deletion, replies, and system messages;
- deterministic per-conversation ordering and cursor-based history;
- realtime delivery through SignalR from a transactional outbox, with typing indicators and single-instance presence;
- reconnection and missed-change synchronization, including membership changes;
- read positions, unread counts, conversation lists with filters and last-message previews;
- host extension points: authorization policy, message validator, audit sink, event subscriber, system messages;
- EF Core persistence for SQL Server and PostgreSQL (SQLite for development), health checks, logging, tracing, and metrics.

Attachments, reactions, threads, notifications, client SDKs, distributed presence, and hosted services will come later.

## How Soga works

### 1. Where Soga lives

Soga is a library inside your own ASP.NET Core application. Your app signs users in; Soga adds the messaging API, the realtime hub, and the tables in your database. Nothing runs outside your infrastructure.

```mermaid
flowchart LR
    subgraph Clients
        W[Web app]
        M[Mobile app]
    end

    subgraph Host["Your ASP.NET Core application"]
        AUTH[Your authentication]
        subgraph Soga
            HTTP["HTTP API<br/>/soga/v1/..."]
            HUB["SignalR hub<br/>/soga/v1/hub"]
            WF["Workflows<br/>conversations, groups,<br/>messages, read state, sync"]
            DISP["Outbox dispatcher<br/>background service"]
        end
        EXT["Your extension points<br/>policies, validators,<br/>audit sink, event subscribers"]
    end

    DB[("Your database<br/>SQL Server, PostgreSQL,<br/>or SQLite")]

    W & M -- "commands: send, edit, read" --> AUTH
    AUTH --> HTTP
    W & M <-- "live events" --> HUB
    HTTP --> WF
    WF <--> EXT
    WF -- "one transaction" --> DB
    DB -- "committed events" --> DISP
    DISP --> HUB
    DISP --> EXT
```

Commands always go over HTTP. The hub only delivers notifications. The database is the source of truth.

### 2. What happens on every request

Every request follows the same path. The user and tenant always come from your trusted server context, never from the request body. A conversation the caller does not belong to looks exactly like one that does not exist.

```mermaid
flowchart TD
    R[Request] --> A{Signed in?}
    A -- No --> E401["401 soga.auth.unauthorized"]
    A -- Yes --> C["Resolve caller<br/>user from your claims,<br/>tenant from your resolver"]
    C --> V{Input valid?}
    V -- No --> E400["400 / 422<br/>soga.request.invalid and others"]
    V -- Yes --> M{Active participant?}
    M -- No --> E404["404 soga.conversation.not_found<br/>same answer as a missing conversation"]
    M -- Yes --> P{"Your policies allow it?<br/>policies can only deny"}
    P -- No --> E403["403 soga.authorization.denied"]
    P -- Yes --> T["Store transaction<br/>lock, apply rules, write"]
    T --> OK[Success response]
```

### 3. Sending a message

A message, its sync entry, and its outbox event are written in one transaction. Nothing is broadcast until that commit succeeds. The response and the live event can arrive in either order; clients match them by ID.

```mermaid
sequenceDiagram
    autonumber
    participant A as Alice's app
    participant S as Soga HTTP API
    participant DB as Database
    participant D as Outbox dispatcher
    participant H as SignalR hub
    participant B as Bob's devices

    A->>S: POST /conversations/{id}/messages<br/>clientMessageId + text
    S->>S: Check sign-in, input, membership, policies
    S->>DB: BEGIN
    S->>DB: Lock conversation, take next sequence
    S->>DB: Insert message, sync change, outbox event
    S->>DB: COMMIT
    S-->>A: 201 Created with id and sequence
    S-)D: Wake up
    D->>DB: Lease pending events
    D->>H: Publish MessageCreated
    H-)B: MessageCreated on every device
    D->>DB: Mark event published
```

### 4. Retrying safely

Every send carries a `clientMessageId` chosen by the client. If the network fails, the client retries with the same ID and can never create a duplicate. The check uses a fingerprint of the text as first sent, so an edit made in between does not break a retry.

```mermaid
flowchart TD
    S["Send with clientMessageId"] --> Q{"ID already used by<br/>this sender here?"}
    Q -- No --> N["201 Created<br/>new message"]
    Q -- Yes --> D{Message deleted?}
    D -- Yes --> T["200 OK<br/>returns the tombstone"]
    D -- No --> F{"Same text as<br/>first sent?"}
    F -- Yes --> O["200 OK<br/>returns the original,<br/>nothing new written"]
    F -- No --> C["409 soga.message.idempotency_conflict<br/>reveals nothing about the message"]
```

### 5. Delivering live events reliably

The outbox makes delivery survive crashes and outages. Events are leased so several servers never publish the same one at once. Failures are retried with growing delays. Delivery is at least once, so clients deduplicate by `eventId`.

```mermaid
stateDiagram-v2
    [*] --> Pending: committed with the change
    Pending --> Leased: dispatcher claims it<br/>oldest first, then commit order
    Leased --> Published: sent to SignalR and<br/>your event subscribers
    Leased --> Pending: failed, retry after<br/>backoff with jitter
    Leased --> Pending: server crashed,<br/>lease expires
    Leased --> Failed: last attempt failed
    Published --> [*]: purged after retention
    Failed --> [*]: kept for inspection,<br/>sync still has the change
```

### 6. Going offline and coming back

Live events are hints. The sync log is the record. A client that was away asks for every change after its saved cursor and gets exactly what it missed, in order, limited to what it may still see.

```mermaid
sequenceDiagram
    autonumber
    participant B as Bob's app
    participant S as Soga HTTP API
    participant H as SignalR hub
    participant A as Alice

    B->>S: GET /sync
    S-->>B: cursor at the current head
    B->>H: Connect, NegotiateProtocol, SubscribeToAll
    Note over B,H: Bob loses the connection
    A->>S: Sends two messages
    Note over B: Nothing is lost.<br/>The changes wait in the sync log.
    B->>H: Reconnect, NegotiateProtocol, SubscribeToAll
    Note over B,H: Only conversations Bob<br/>can still access are restored
    B->>S: GET /sync?cursor=saved
    S-->>B: Both messages in order, plus a new cursor
    B->>B: Skip any eventId already shown live
    B->>S: POST /conversations/{id}/read
```

### 7. Groups and roles

A group always keeps at least one owner. Membership changes are decided inside one locked transaction, so two simultaneous changes can never leave a group without an owner. Removing someone cuts off their HTTP access, their sync, and every live connection on every device at once.

```mermaid
flowchart LR
    O(["Owner"]) -->|"add or remove anyone,<br/>grant any role,<br/>change roles"| G((Group))
    AD(["Admin"]) -->|"add or remove members,<br/>rename, archive,<br/>delete messages"| G
    ME(["Member"]) -->|"send, edit and delete<br/>own messages, leave"| G
```

```mermaid
stateDiagram-v2
    [*] --> Active: added or creates the group
    Active --> Active: role changed by an owner
    Active --> Left: leaves or is removed
    Left --> Active: added again, joins caught up
    note right of Active
        The last owner cannot leave
        or be demoted
    end note
```

### 8. A message's life

The sequence never changes, so ordering stays stable. Deleting erases the text but keeps a tombstone, so the conversation has no gaps and replies can show "deleted". System messages come only from trusted server code and cannot be edited or deleted by anyone.

```mermaid
stateDiagram-v2
    [*] --> Sent: gets the next sequence
    Sent --> Edited: author, within the edit window
    Edited --> Edited: edited again
    Sent --> Deleted: author, or a group owner or admin
    Edited --> Deleted
    Deleted --> [*]: tombstone keeps its sequence,<br/>text and edit history erased
```

### 9. The tables Soga adds

Your application owns the migrations. Every key starts with the tenant, so tenants can never see each other's data. Soga never references your user table.

```mermaid
erDiagram
    SogaConversations ||--o{ SogaParticipants : has
    SogaConversations ||--o{ SogaMessages : contains
    SogaConversations ||--o{ SogaChanges : records
    SogaMessages ||--o{ SogaMessageEdits : "keeps history of"
    SogaTenantState ||--o{ SogaChanges : numbers

    SogaConversations {
        string TenantId PK
        guid Id PK
        string DirectKey UK "one direct chat per pair and reference"
        string Kind "Direct or Group"
        string Title
        string ExternalReference "order, booking, ticket"
        long LastSequence "last message number"
        datetime LastActivityAt
        datetime ArchivedAt
    }
    SogaParticipants {
        string TenantId PK
        guid ConversationId PK
        string UserId PK "your user ID, case-sensitive"
        string Role "Member, Admin, Owner"
        long LastReadSequence "never moves backward"
        datetime LeftAt
    }
    SogaMessages {
        string TenantId PK
        guid Id PK
        guid ConversationId
        long Sequence UK "gapless per conversation"
        string SenderId
        string ClientMessageId UK "retry key per sender"
        string Content "erased on delete"
        string ContentFingerprint "text as first sent"
        datetime EditedAt
        datetime DeletedAt
        guid ReplyToMessageId
    }
    SogaMessageEdits {
        string TenantId PK
        guid MessageId PK
        int Version PK
        string PreviousContent
    }
    SogaChanges {
        string TenantId PK
        long Sequence PK "commit order, read by sync"
        guid EventId UK "same ID as the outbox event"
        guid ConversationId
        string TargetUserId "tells removed users"
        string Payload
    }
    SogaTenantState {
        string TenantId PK
        long LastChangeSequence
    }
    SogaOutbox {
        string TenantId PK
        guid Id PK "the event ID"
        string EventType
        string Payload
        datetime AvailableAt "next attempt"
        datetime LeaseUntil
        int AttemptCount
        datetime PublishedAt
        datetime FailedAt
    }
```

## Packages

| Package | Purpose |
|---|---|
| `Soga` | The ASP.NET Core messaging framework, protocol, SignalR integration, extension points, and in-memory development provider |
| `Soga.EntityFrameworkCore` | Relational database persistence using EF Core |

`Soga.Client` and a TypeScript client will be developed after the backend protocol is stable.

## Usage

```bash
dotnet add package Soga
dotnet add package Soga.EntityFrameworkCore
```

```csharp
builder.Services
    .AddSoga(options =>
    {
        options.RoutePrefix = "/soga/v1";
        options.Groups.MaximumParticipants = 256;
        options.Messages.MaximumTextLength = 8_000;
    })
    .UseEntityFrameworkCore<AppDbContext>();

app.MapSoga();
```

```csharp
// In your DbContext (SQL Server needs a case-sensitive collation for identity columns; see the deployment guide):
protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddSoga();
```

Calling `MapSoga()` maps Soga's HTTP API and SignalR hub inside the developer's own application. `/soga/v1` is a configurable default prefix; it is not an external Soga service address. Your application owns the database migrations.

See [Getting started](docs/getting-started/README.md) for the full walkthrough, [Concepts](docs/concepts/README.md) for the HTTP API, roles, events, and error codes, and the [V1 to V2 migration guide](docs/migration/v1-to-v2.md) if you used the first preview.

## Build and test

```bash
dotnet restore
dotnet build -warnaserror
dotnet test
dotnet pack src/Soga/Soga.csproj -c Release -o artifacts/packages
dotnet pack src/Soga.EntityFrameworkCore/Soga.EntityFrameworkCore.csproj -c Release -o artifacts/packages
```

The SQL Server and PostgreSQL test suites run only when `SOGA_TEST_SQLSERVER` or `SOGA_TEST_POSTGRES` point at a server that may create databases; otherwise they are skipped.

```bash
dotnet run --project samples/Soga.MinimalSample
dotnet run --project samples/Soga.TeamChatSample
dotnet run --project samples/Soga.MarketplaceSample
```

## Design principles

- The database is the source of truth.
- Messages and changes are stored before realtime publication.
- SignalR is transport, not message storage.
- The authenticated user is determined by the host application.
- Every protected operation is authorized, and host rules can only restrict.
- Message sends are safe to retry.
- Disconnected clients can recover missed durable changes.
- Typing and presence are advisory and never grant access.
- Public contracts are separate from persistence entities.
- Single-server correctness comes before distributed scale-out.

## Documentation

- [Getting started](docs/getting-started/README.md)
- [Concepts, HTTP API, events, and errors](docs/concepts/README.md)
- [Security and threat model](docs/security/README.md)
- [Deployment and operations](docs/deployment/README.md)
- [V1 to V2 migration](docs/migration/v1-to-v2.md)
- [Architecture decisions](docs/decisions/)
- [Architecture and engineering guide](SOGA_ARCHITECTURE_AND_ENGINEERING_GUIDE.md)

## Contributing

Read `CONTRIBUTING.md`. Major architecture or public API changes should be discussed before implementation and documented through architecture decision records.

## Security

Soga requires authentication by default and does not trust client-supplied sender identities. Report vulnerabilities privately as described in `SECURITY.md`.

Soga uses HTTPS for transport security and does not provide end-to-end encryption.

## Ownership

Soga is created and maintained by Zhuwa Africa Limited.

## License

Soga is licensed under the Apache License 2.0. See `LICENSE` for details.
