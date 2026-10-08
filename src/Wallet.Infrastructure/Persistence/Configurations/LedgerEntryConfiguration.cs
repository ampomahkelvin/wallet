using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wallet.Domain;

namespace Wallet.Infrastructure.Persistence.Configurations;

public class LedgerEntryConfiguration : IEntityTypeConfiguration<Wallet.Domain.LedgerEntry>
{
    public void Configure(EntityTypeBuilder<Wallet.Domain.LedgerEntry> b)
    {
        b.ToTable("ledger_entries", t =>
        {
            t.HasCheckConstraint("ck_ledger_entries_amount_nonzero",
                """ "Amount" <> 0 """);
        });

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.TransferId).IsRequired();
        b.Property(x => x.WalletId).IsRequired();
        b.Property(x => x.Amount).HasColumnType("bigint").IsRequired();
        b.Property(x => x.BalanceAfter).HasColumnType("bigint").IsRequired();

        b.HasIndex(x => new { x.TransferId, x.WalletId }).IsUnique();
        b.HasIndex(x => new { x.WalletId, x.Id }).IsDescending(true, false);

        b.HasOne<Wallet.Domain.Transfer>()
            .WithMany(x => x.Entries)
            .HasForeignKey(x => x.TransferId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<Wallet.Domain.Wallet>()
            .WithMany()
            .HasForeignKey(x => x.WalletId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}