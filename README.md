# Wallet Ledger

A double-entry payments ledger API with idempotent writes, ordered row-level locking, a transactional outbox that publishes events to RabbitMQ, and Redis-backed rate limiting.

## Architecture

```
Client ──HTTP──► API (Express) ──► Redis (rate limiting)
                  │
                  ├──► PostgreSQL (idempotency + transfer + entries + balances + outbox)
                  │
                  └──► Outbox relay ──► RabbitMQ ──► Consumer (dedupe)
```

## Quick Start

```bash
docker compose up
```

This starts:
- PostgreSQL 17
- RabbitMQ 4 (management UI at http://localhost:15672, guest/guest)
- Redis 7
- Prometheus (http://localhost:9090)
- Grafana (http://localhost:3000, admin/admin)

## API

All mutating endpoints require `Idempotency-Key` and `X-Client-Id` headers.

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

## Design

See [DESIGN.md](DESIGN.md) for the full design document.

## Testing

```bash
dotnet test
```

## Load Testing

```bash
k6 run k6/transfers.js
```

## Observability

- `/metrics` — Prometheus metrics
- `/health` — liveness
- `/health/deep` — database connectivity
- Grafana dashboard at http://localhost:3000