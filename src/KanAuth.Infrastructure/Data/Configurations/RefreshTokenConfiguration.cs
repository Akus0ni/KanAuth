using KanAuth.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KanAuth.Infrastructure.Data.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.HasKey(rt => rt.Id);

        builder.Property(rt => rt.Token)
            .IsRequired()
            .HasMaxLength(200);

        builder.HasIndex(rt => rt.Token)
            .IsUnique();

        builder.Property(rt => rt.CreatedByIp).IsRequired().HasMaxLength(45);
        builder.Property(rt => rt.RevokedByIp).HasMaxLength(45);
        builder.Property(rt => rt.ReplacedByToken).HasMaxLength(200);
    }
}
