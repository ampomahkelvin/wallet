using Prometheus;

namespace Wallet.Infrastructure.Observability;

public static class LedgerMetrics
{
    public static readonly Counter TransfersTotal = Metrics.CreateCounter(
        "wallet_transfers_total",
        "Total number of transfers executed");

    public static readonly Gauge OutboxUnpublishedCount = Metrics.CreateGauge(
        "wallet_outbox_unpublished",
        "Number of unpublished outbox events");

    public static readonly Gauge OutboxOldestUnpublishedSeconds = Metrics.CreateGauge(
        "wallet_outbox_oldest_age_seconds",
        "Age in seconds of the oldest unpublished outbox event");

    public static readonly Gauge InvariantViolations = Metrics.CreateGauge(
        "wallet_invariant_violations",
        "Number of wallets whose cached balance disagrees with its ledger entries");

    public static readonly Counter RateLimiterRejections = Metrics.CreateCounter(
        "wallet_rate_limit_rejections_total",
        "Total number of requests rejected by the rate limiter");

    public static readonly Counter RateLimiterFailOpen = Metrics.CreateCounter(
        "wallet_rate_limiter_fail_open_total",
        "Total number of times the rate limiter failed open");
}