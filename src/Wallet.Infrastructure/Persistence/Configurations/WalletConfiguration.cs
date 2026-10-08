using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wallet.Domain;

namespace Wallet.Infrastructure.Persistence.Configurations;

public class WalletConfiguration : IEntityTypeConfiguration<Wallet.Domain.Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet.Domain.Wallet> b)
    {
        b.ToTable("wallets", t =>
        {
            t.HasCheckConstraint("ck_wallets_balance_non_negative",
                """ "Kind" <> 1 OR "Balance" >= 0 """);
            t.HasCheckConstraint("ck_wallets_owner_matches_kind",
                """ ("Kind" = 1) = ("OwnerId" IS NOT NULL) """);
        });

        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.Id, x.Currency });
        b.Property(x => x.Kind).HasConversion<int>().IsRequired();
        b.Property(x => x.OwnerId).HasMaxLength(100);
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(x => x.Balance).HasColumnType("bigint").HasDefaultValue(0L);

        b.HasIndex(x => new { x.OwnerId, x.Currency })
            .IsUnique()
            .HasFilter(""" "Kind" = 1 """);
        
        b.HasIndex(x => new { x.Currency })
            .IsUnique()
            .HasFilter(""" "Kind" = 2 """);
    }
}