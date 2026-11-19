using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class CategoryRuleConfiguration : IEntityTypeConfiguration<CategoryRule>
{
    public void Configure(EntityTypeBuilder<CategoryRule> builder)
    {
        builder.ToTable("CategoryRules");

        builder.HasKey(r => r.Id);

        // DB-level backstop against pathologically long REGEX/CONTAINS input (T-04-01);
        // 04-04's REGEX timeout guard is the primary defense, this is defense-in-depth.
        builder.Property(r => r.MatchValue)
            .HasMaxLength(500);

        builder.Property(r => r.IsActive)
            .HasDefaultValue(true);

        builder.Property(r => r.MarkAsTransfer)
            .HasDefaultValue(false);

        builder.HasIndex(r => r.UserId);

        // The exact access pattern 04-04's ordered rule lookup and 04-05's list/reorder
        // endpoints use.
        builder.HasIndex(r => new { r.UserId, r.Priority });

        builder.HasIndex(r => r.CategoryId);
    }
}
