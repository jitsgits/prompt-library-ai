using Microsoft.EntityFrameworkCore;
using PromptBE.Models;

namespace PromptBE.Data;

public class PromptDbContext : DbContext
{
    public PromptDbContext(DbContextOptions<PromptDbContext> options) : base(options) { }

    public DbSet<Prompt> Prompts => Set<Prompt>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TagPrompt> TagPrompts => Set<TagPrompt>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryPrompt> CategoryPrompts => Set<CategoryPrompt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Prompt Configuration
        modelBuilder.Entity<Prompt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(250).IsRequired();
            entity.Property(e => e.PromptText).HasColumnName("Prompt").IsRequired(); // nvarchar(max) by default
            entity.Property(e => e.CreatedOn).IsRequired();
        });

        // Tag Configuration
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(50).IsRequired();
        });

        // TagPrompt Configuration (Many-to-Many Join Table)
        modelBuilder.Entity<TagPrompt>(entity =>
        {
            entity.HasKey(tp => new { tp.PromptId, tp.TagId });
            entity.ToTable("TagPrompts");

            entity.HasOne(tp => tp.Prompt)
                  .WithMany(p => p.TagPrompts)
                  .HasForeignKey(tp => tp.PromptId);

            entity.HasOne(tp => tp.Tag)
                  .WithMany(t => t.TagPrompts)
                  .HasForeignKey(tp => tp.TagId);
        });

        // Category Configuration
        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(50).IsRequired();
        });

        // CategoryPrompt Configuration (Many-to-Many Join Table)
        modelBuilder.Entity<CategoryPrompt>(entity =>
        {
            entity.HasKey(cp => new { cp.PromptId, cp.CategoryId });
            entity.ToTable("CategoryPrompts");

            entity.HasOne(cp => cp.Prompt)
                  .WithMany(p => p.CategoryPrompts)
                  .HasForeignKey(cp => cp.PromptId);

            entity.HasOne(cp => cp.Category)
                  .WithMany(c => c.CategoryPrompts)
                  .HasForeignKey(cp => cp.CategoryId);
        });
    }
}
