# Wallet Ledger

A double-entry payments ledger API with idempotent writes, ordered row-level locking, API-key authentication, a transactional outbox that publishes events to RabbitMQ, and Redis-backed rate limiting.

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

The checked-in development keys are `dev-key-client-1` → `client-1` and
`dev-key-client-2` → `client-2`. Override them with `ApiKeys__<key>` environment
variables in any real deployment.

## API

All `/api` endpoints and `/admin/reconcile` require an `X-Api-Key` header. The
API key identifies the client, and a client owns its wallets. Mutating endpoints
also require `Idempotency-Key`. Replaying a key returns the original response
with `Idempotent-Replayed: true`; reusing a key with a different payload returns
`422`.

### Create a wallet

```bash
curl -X POST http://localhost:5158/api/wallets \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key-client-1" \
  -H "Idempotency-Key: key-1" \
  -d '{"currency": "GHS"}'
```

### Deposit

```bash
curl -X POST http://localhost:5158/api/wallets/{id}/deposits \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key-client-1" \
  -H "Idempotency-Key: key-2" \
  -d '{"amount": 1000}'
```

### Transfer

```bash
curl -X POST http://localhost:5158/api/transfers \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key-client-1" \
  -H "Idempotency-Key: key-3" \
  -d '{"sourceWalletId": "...", "destinationWalletId": "...", "currency": "GHS", "amount": 100}'
```

### Reverse a transfer

Only the owner of the original destination wallet can reverse a transfer.

```bash
curl -X POST http://localhost:5158/api/transfers/{id}/reverse \
  -H "X-Api-Key: dev-key-client-2" \
  -H "Idempotency-Key: key-4"
```

### Get wallet entries

```bash
curl http://localhost:5158/api/wallets/{id}/entries?limit=50 \
  -H "X-Api-Key: dev-key-client-1"
```

## Observability

- `/metrics` — Prometheus metrics (outbox lag, outbox backlog, invariant violations, transfer rate, rate-limit rejections)
- `/health` — liveness
- `/health/deep` — database, broker, and Redis connectivity
- `/admin/reconcile` — runs the ledger invariant queries and reports violations (API key required)
- Grafana dashboard at http://localhost:3000

## Testing

```bash
dotnet test
```

Tests run against real PostgreSQL and RabbitMQ via Testcontainers, including concurrency, failure-injection, authentication, and idempotency atomicity tests.

## Load Testing

```bash
docker compose down -v
RATE_LIMIT_MAX=1000000 docker compose up
k6 run \
  -e CLIENT_1_KEY=dev-key-client-1 \
  -e CLIENT_2_KEY=dev-key-client-2 \
  k6/transfers.js
```

The setup phase creates one wallet per client, and each client may only own one
wallet per currency, so start from a clean database before each run.

## Known limitations

- **Authentication is config-based.** API keys are read from configuration and
  mapping is key → client. There is no key expiry, rotation endpoint, or role
  model yet.
- A client owns at most one wallet per currency.
- Reversals are full and cannot be reversed again; no partial reversals.
- Single currency per transfer; no fees or conversion.
- A single relay instance preserves outbox ordering; scaling it trades away ordering.
- Idempotency of business errors is not cached (a retry after a failed deposit re-evaluates).
