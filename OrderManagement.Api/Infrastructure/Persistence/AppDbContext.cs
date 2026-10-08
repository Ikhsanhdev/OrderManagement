using Microsoft.EntityFrameworkCore;
using OrderManagement.Api.Domain.Entities;

namespace OrderManagement.Api.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(x => x.StockQuantity)
                .IsRequired();

            entity.Property(x => x.Price)
                .HasPrecision(18, 2);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.CustomerId)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(x => x.ShippingAddress)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(x => x.Status)
                .IsRequired();

            entity.Property(x => x.IdempotencyKey)
                .HasMaxLength(200);

            entity.HasIndex(x => x.IdempotencyKey)
                .IsUnique();

            entity.Property(x => x.Version)
                .IsRequired()
                .HasDefaultValue(1L);

            entity.HasMany(x => x.Items)
                .WithOne(x => x.Order)
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .ValueGeneratedNever();

            entity.Property(x => x.Quantity)
                .IsRequired();

            entity.Property(x => x.UnitPrice)
                .HasPrecision(18, 2);

            entity.HasOne(x => x.Product)
                .WithMany(x => x.OrderItems)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(x => new { x.OrderId, x.ProductId })
                .IsUnique();
        });
    }
}
