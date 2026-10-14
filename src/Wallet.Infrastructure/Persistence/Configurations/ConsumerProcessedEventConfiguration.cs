using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wallet.Domain;

namespace Wallet.Infrastructure.Persistence.Configurations;

public class ConsumerProcessedEventConfiguration : IEntityTypeConfiguration<ConsumerProcessedEvent>
{
    public void Configure(EntityTypeBuilder<ConsumerProcessedEvent> b)
    {
        b.ToTable("consumer_processed_events");
        b.HasKey(x => new { x.EventId, x.ConsumerName });
        b.Property(x => x.EventId).IsRequired();
        b.Property(x => x.ConsumerName).HasMaxLength(100).IsRequired();
        b.Property(x => x.ProcessedAt).IsRequired();
    }
}