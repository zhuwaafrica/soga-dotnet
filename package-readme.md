# Soga

> Build conversations. Own your infrastructure.

Soga is an open-source, self-hosted messaging framework for ASP.NET Core. It adds the application layer you need above SignalR: direct and group conversations, persistent messages, roles, authorization, history, editing and deleting, read state, realtime events, and recovery after a disconnect. It runs inside your own application, with your users and your database.

## Requirements

- .NET 10 or later.
- An ASP.NET Core application that already signs users in.
- For production: SQL Server or PostgreSQL through `Soga.EntityFrameworkCore`.

## Installation

```bash
dotnet add package Soga
dotnet add package Soga.EntityFrameworkCore
```

## Usage

```csharp
builder.Services
    .AddSoga(options => options.RoutePrefix = "/soga/v1")
    .UseEntityFrameworkCore<AppDbContext>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapSoga();   // HTTP API and SignalR hub inside your app
```

```csharp
// In your DbContext; then create a migration as usual.
protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddSoga();
```

Clients send commands over HTTP (`POST /soga/v1/conversations/{id}/messages` with a `clientMessageId`, so a retry never duplicates) and receive live events from the SignalR hub at `/soga/v1/hub`. After a disconnect, `GET /soga/v1/sync?cursor=...` returns every change they missed.

## Guarantees

- Only authorized users can access a conversation, over HTTP and SignalR alike.
- An accepted message is stored before anything is announced.
- A retry never creates a duplicate.
- Messages keep a fixed order that editing and deleting never change.
- A disconnected client recovers everything it missed that it may still see.
- A removed participant loses access immediately, on every device.

## Documentation

- [Getting started](https://github.com/zhuwaafrica/soga-dotnet/blob/main/docs/getting-started/README.md)
- [Concepts, HTTP API, events and error codes](https://github.com/zhuwaafrica/soga-dotnet/blob/main/docs/concepts/README.md)
- [How Soga works](https://github.com/zhuwaafrica/soga-dotnet/blob/main/docs/architecture.md)
- [Deployment](https://github.com/zhuwaafrica/soga-dotnet/blob/main/docs/deployment/README.md)
- [Source code and issues](https://github.com/zhuwaafrica/soga-dotnet)

Soga is created and maintained by Zhuwa Africa Limited and licensed under the Apache License 2.0.
