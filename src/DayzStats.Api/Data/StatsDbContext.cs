using Microsoft.EntityFrameworkCore;

namespace DayzStats.Api.Data;

public class StatsDbContext(DbContextOptions<StatsDbContext> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Death> Deaths => Set<Death>();
    public DbSet<ImportedFile> ImportedFiles => Set<ImportedFile>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Player>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).HasMaxLength(64);
            e.HasIndex(p => p.Name);
            e.HasMany(p => p.Sessions).WithOne().HasForeignKey(s => s.PlayerId);
            e.HasMany(p => p.Deaths).WithOne().HasForeignKey(d => d.PlayerId);
        });

        model.Entity<Session>().HasIndex(s => s.Start);

        model.Entity<Death>(e =>
        {
            e.HasIndex(d => d.Time);
            e.HasIndex(d => d.KillerId);
            e.Property(d => d.Cause).HasMaxLength(64);
            e.Property(d => d.Weapon).HasMaxLength(64);
        });

        model.Entity<ImportedFile>().HasIndex(f => f.FileName).IsUnique();
    }
}
