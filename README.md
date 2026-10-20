# Wallet Ledger

A double-entry payments ledger API with idempotent writes, ordered row-level locking, a transactional outbox that publishes events to RabbitMQ, and Redis-backed rate limiting.

Stack: C#, ASP.NET Core, EF Core (Npgsql), PostgreSQL 17, Redis 7, RabbitMQ 4, xUnit + Testcontainers, k6, Prometheus, Grafana.

## Architecture

```
Client ──HTTP──► API (ASP.NET Core) ──► Redis (rate limiting, fails open)
                   │
                   ├──► PostgreSQL (idempotency + transfer + entries + balances + outbox)
                   │
                   └──► Outbox relay ──► RabbitMQ ──► Consumer (dedupe on event_id)
```

Correctness never depends on RabbitMQ or Redis: if the broker is down, transfers still succeed and events accumulate in the outbox; if Redis is down, rate limiting fails open.

## Quick Start

```bash
docker compose up
```

This starts:
- **API** on http://localhost:5158 (migrations run on startup)
- PostgreSQL 17
- RabbitMQ 4 (management UI at http://localhost:15672, guest/guest)
- Redis 7
- Prometheus (http://localhost:9090)
- Grafana (http://localhost:3000, admin/admin)

For local development without Docker:

```bash
docker compose up postgres rabbitmq redis
dotnet run --project src/Wallet.Api
```

## API

All mutating endpoints require `Idempotency-Key` and `X-Client-Id` headers. Replaying a key returns the original response with `Idempotent-Replayed: true`; reusing a key with a different payload returns `422`.

### Create a wallet

```bash
curl -X POST http://localhost:5158/api/wallets \
  -H "Content-Type: application/json" \
  -H "X-Client-Id: client-1" \
  -H "Idempotency-Key: key-1" \
  -d '{"ownerId": "user-1", "currency": "GHS"}'
```

### Deposit

```bash
curl -X POST http://localhost:5158/api/wallets/{id}/deposits \
  -H "Content-Type: application/json" \
  -H "X-Client-Id: client-1" \
  -H "Idempotency-Key: key-2" \
  -d '{"amount": 1000}'
```

### Transfer

```bash
curl -X POST http://localhost:5158/api/transfers \
  -H "Content-Type: application/json" \
  -H "X-Client-Id: client-1" \
  -H "Idempotency-Key: key-3" \
  -d '{"sourceWalletId": "...", "destinationWalletId": "...", "currency": "GHS", "amount": 100}'
```

### Reverse a transfer

```bash
curl -X POST http://localhost:5158/api/transfers/{id}/reverse \
  -H "X-Client-Id: client-1" \
  -H "Idempotency-Key: key-4"
```

### Get wallet entries

```bash
curl http://localhost:5158/api/wallets/{id}/entries?limit=50
```

## Observability

- `/metrics` — Prometheus metrics (outbox lag, outbox backlog, invariant violations, transfer rate, rate-limit rejections)
- `/health` — liveness
- `/health/deep` — database, broker, and Redis connectivity
- `/admin/reconcile` — runs the ledger invariant queries and reports violations
- Grafana dashboard at http://localhost:3000

## Testing

```bash
dotnet test
```

Tests run against real PostgreSQL and RabbitMQ via Testcontainers, including concurrency, failure-injection, and idempotency atomicity tests.

## Load Testing

```bash
docker compose up
k6 run k6/transfers.js
```

## Known limitations

- **Authentication is out of scope.** `X-Client-Id` is a caller-supplied header; there is no ownership check on wallets. Do not expose this API publicly without adding auth.
- Reversals are full and cannot be reversed again; no partial reversals.
- Single currency per transfer; no fees or conversion.
- A single relay instance preserves outbox ordering; scaling it trades away ordering.
- Idempotency of business errors is not cached (a retry after a failed deposit re-evaluates).