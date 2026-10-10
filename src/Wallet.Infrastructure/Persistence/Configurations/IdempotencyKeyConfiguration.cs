using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wallet.Domain;

namespace Wallet.Infrastructure.Persistence.Configurations;

public class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> b)
    {
        b.ToTable("idempotency_keys", t =>
        {
            t.HasCheckConstraint("ck_idempotency_response_complete",
                """ ("ResponseStatus" IS NULL) = ("ResponseBody" IS NULL) """);
        });

        b.HasKey(x => new { x.ClientId, x.Key });
        b.Property(x => x.ClientId).HasMaxLength(100).IsRequired();
        b.Property(x => x.Key).HasMaxLength(200).IsRequired();
        b.Property(x => x.RequestHash).IsRequired();
        b.Property(x => x.ResponseStatus);
        b.Property(x => x.ResponseBody);
        b.Property(x => x.ExpiresAt).IsRequired();

        b.HasIndex(x => x.ExpiresAt);
    }
}