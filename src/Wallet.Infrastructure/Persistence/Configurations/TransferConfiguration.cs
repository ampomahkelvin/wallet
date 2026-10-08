using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wallet.Domain;

namespace Wallet.Infrastructure.Persistence.Configurations;

public class TransferConfiguration : IEntityTypeConfiguration<Wallet.Domain.Transfer>
{
    public void Configure(EntityTypeBuilder<Wallet.Domain.Transfer> b)
    {
        b.ToTable("transfers", t =>
        {
            t.HasCheckConstraint("ck_transfers_distinct_wallets",
                """ "SourceWalletId" <> "DestinationWalletId" """);
            t.HasCheckConstraint("ck_transfers_reversal_link",
                """ ("Type" = 4) = ("ReversesTransferId" IS NOT NULL) """);
            t.HasCheckConstraint("ck_transfers_amount_positive",
                """ "Amount" > 0 """);
        });

        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<int>().IsRequired();
        b.Property(x => x.SourceWalletId).IsRequired();
        b.Property(x => x.DestinationWalletId).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(x => x.Amount).HasColumnType("bigint").IsRequired();
        b.Property(x => x.ReversesTransferId);

        b.HasIndex(x => x.ReversesTransferId).IsUnique().HasFilter(""" "ReversesTransferId" IS NOT NULL """);

        b.HasOne<Wallet.Domain.Wallet>()
            .WithMany()
            .HasForeignKey(x => new { x.SourceWalletId, x.Currency })
            .HasPrincipalKey(w => new { w.Id, w.Currency })
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<Wallet.Domain.Wallet>()
            .WithMany()
            .HasForeignKey(x => new { x.DestinationWalletId, x.Currency })
            .HasPrincipalKey(w => new { w.Id, w.Currency })
            .OnDelete(DeleteBehavior.Restrict);

        b.Navigation(x => x.Entries).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}