# Coboard — Collaborative Whiteboard

Personal project to sharpen .NET backend skills via a distributed-systems playground.

## Architecture

Three-service split (modular enough to learn, not so many it stalls shipping):

| Service        | Owns                                       | Persistence |
|----------------|--------------------------------------------|-------------|
| `BoardService` | boards, shapes, history, undo/redo         | Postgres    |
| `PresenceService` | cursors, online users, ephemeral state  | Redis       |
| `AuthService`  | users, credentials, JWT issuance           | Postgres    |

Each service is its own solution/project, deployable independently, with its own database (or schema).

## Tech Stack

- .NET 10 (LTS) + EF Core 10
- PostgreSQL 16 (via Docker)
- Redis 7 (via Docker)
- SignalR for real-time sync (with `ISyncTransport` adapter so we can swap to raw WebSockets later)
- xUnit + FluentAssertions + Testcontainers for tests
- YARP for API gateway (added when 3+ services need routing)

## Real-Time Sync

Current: **server-authoritative op relay** via SignalR.

The `ISyncTransport` interface lets us swap between:
- `SignalRTransport` (default)
- `RawWebSocketTransport` (for non-MS clients / benchmarks)
- `LoopbackTransport` (tests)

All three serialize the same `WireEvent { op, clientId, clock, parentOpId }` envelope.

### Future: CRDT (Yjs / Automerge)

When we're ready to tackle proper conflict-free collab, Yjs/Automerge is the "right" answer for simultaneous edits. It's a **separate paradigm** though — client owns merge logic, server relays opaque binary updates. This is a v2 learning track, not a drop-in transport. See `docs/architecture/sync-paradigms.md` (TODO) for details.

## EF Core Patterns Used (Board Service)

All seven are wired in to force reps on each:
- Compiled queries for hot shape reads
- Manual history table (Npgsql lacks SQL Server temporal tables)
- `ExecuteUpdateAsync` for bulk ops
- Owned types for shape geometry
- Value comparers on `Board.Tags` (jsonb primitive collection)
- DbContext pooling + NoTracking default
- Interceptors for soft-delete + audit fields

## Local Dev

```sh
docker compose up -d   # postgres + redis
dotnet build
dotnet test
```
