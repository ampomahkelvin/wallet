using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wallet.Domain;

namespace Wallet.Infrastructure.Persistence.Configurations;

public class LedgerTransactionConfiguration : IEntityTypeConfiguration<LedgerTransaction>
{
    public void Configure(EntityTypeBuilder<LedgerTransaction> b)
    {
        b.ToTable("ledger_transactions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Reference).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.Reference).IsUnique();

        b.HasMany(x => x.Entries)
         .WithOne()
         .HasForeignKey(e => e.TransactionId)
         .OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Entries).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> b)
    {
        b.ToTable("ledger_entries", t =>
            t.HasCheckConstraint("ck_ledger_entries_amount_positive", """ "Amount" > 0 """));

        b.HasKey(x => x.Id);
        b.Property(x => x.Direction).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Amount).HasColumnType("bigint");

        b.HasOne<Account>()
         .WithMany()
         .HasForeignKey(x => x.AccountId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.AccountId);
        b.HasIndex(x => x.TransactionId);
    }
}