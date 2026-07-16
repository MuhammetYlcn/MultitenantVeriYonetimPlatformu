using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.HasIndex(t => t.Slug).IsUnique();
            tenant.Property(t => t.Name).HasMaxLength(200);
            tenant.Property(t => t.Slug).HasMaxLength(100);
        });

        modelBuilder.Entity<User>(user =>
        {
            user.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();
            user.Property(u => u.Email).HasMaxLength(320);
            user.Property(u => u.Role).HasMaxLength(50);
        });
    }
}
