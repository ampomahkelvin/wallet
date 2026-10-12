using Microsoft.EntityFrameworkCore;
using Wallet.Domain;

namespace Wallet.Infrastructure.Persistence;

public class WalletDbContext(DbContextOptions<WalletDbContext> options) : DbContext(options)
{
    public DbSet<Wallet.Domain.Wallet> Wallets => Set<Wallet.Domain.Wallet>();
    public DbSet<Wallet.Domain.Transfer> Transfers => Set<Wallet.Domain.Transfer>();
    public DbSet<Wallet.Domain.LedgerEntry> LedgerEntries => Set<Wallet.Domain.LedgerEntry>();
    public DbSet<Wallet.Domain.IdempotencyKey> IdempotencyKeys => Set<Wallet.Domain.IdempotencyKey>();
    public DbSet<Wallet.Domain.OutboxEvent> OutboxEvents => Set<Wallet.Domain.OutboxEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(WalletDbContext).Assembly);
}