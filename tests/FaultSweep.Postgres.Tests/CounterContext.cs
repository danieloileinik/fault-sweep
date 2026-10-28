using Microsoft.EntityFrameworkCore;

namespace FaultSweep.Postgres.Tests;

public sealed class CounterContext(DbContextOptions<CounterContext> options) : DbContext(options)
{
    public DbSet<Counter> Counters => Set<Counter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Counter>(entity =>
        {
            entity.ToTable("counters");
            entity.HasKey(counter => counter.Id);
            entity.Property(counter => counter.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(counter => counter.Value).HasColumnName("value");
        });
    }
}
