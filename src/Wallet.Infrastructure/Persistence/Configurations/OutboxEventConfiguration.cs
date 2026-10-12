using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wallet.Domain;

namespace Wallet.Infrastructure.Persistence.Configurations;

public class OutboxEventConfiguration : IEntityTypeConfiguration<OutboxEvent>
{
    public void Configure(EntityTypeBuilder<OutboxEvent> b)
    {
        b.ToTable("outbox_events");

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.TransferId).IsRequired();
        b.Property(x => x.Type).HasMaxLength(50).IsRequired();
        b.Property(x => x.Payload).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();
        b.Property(x => x.PublishedAt);

        b.HasIndex(x => new { x.TransferId, x.Type }).IsUnique();
        b.HasIndex(x => x.Id).HasFilter(" \"PublishedAt\" IS NULL ");

        b.HasOne<Transfer>()
            .WithMany()
            .HasForeignKey(x => x.TransferId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}