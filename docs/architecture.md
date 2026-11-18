# How Soga works

This page explains Soga's design with diagrams: where it runs, what happens on each request, and how messages, retries, live events, reconnection, groups and storage work. To use Soga, start with [Getting started](getting-started/README.md); the binding engineering rules are in the [architecture and engineering guide](../SOGA_ARCHITECTURE_AND_ENGINEERING_GUIDE.md) and the [decision records](decisions/).

## A conversation from start to finish

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

## 1. Where Soga lives

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

## 2. What happens on every request

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

## 3. Sending a message

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

## 4. Retrying safely

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

## 5. Delivering live events reliably

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

## 6. Going offline and coming back

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

## 7. Groups and roles

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

## 8. A message's life

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

## 9. The tables Soga adds

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
