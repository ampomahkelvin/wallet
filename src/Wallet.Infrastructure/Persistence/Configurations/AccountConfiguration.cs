using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wallet.Domain;

namespace Wallet.Infrastructure.Persistence.Configurations;

public class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        // Database-level safety net: a bug in code can't push a user wallet negative.
        b.ToTable("accounts", t => t.HasCheckConstraint(
            "ck_accounts_balance_non_negative",
            """ "Type" <> 'UserWallet' OR "Balance" >= 0 """));

        b.HasKey(x => x.Id);
        b.Property(x => x.OwnerId).HasMaxLength(100).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Balance).HasColumnType("bigint");

        b.HasIndex(x => x.OwnerId);
    }
}