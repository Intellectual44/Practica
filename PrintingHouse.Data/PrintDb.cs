using PrintingHouse.Core;
using Microsoft.EntityFrameworkCore;

namespace PrintingHouse.Data;

public sealed class PrintDb(DbContextOptions<PrintDb> options) : DbContext(options)
{
    public DbSet<PrintUser> Users => Set<PrintUser>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<PrintOrder> Orders => Set<PrintOrder>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<PrintUser>().HasIndex(user => user.Login).IsUnique();
        model.Entity<Customer>().HasIndex(customer => customer.Name);
        model.HasDbFunction(typeof(PrintStore).GetMethod(nameof(PrintStore.PrintContains))!)
            .HasName("print_contains").IsBuiltIn();
        model.Entity<PrintUser>().HasData(
            new PrintUser { Id = 1, Login = "press.admin", DisplayName = "Алёна Воронова", Role = Roles.Admin, PasswordHash = "5037c4ccd30e07d196ee34f6b7ac54af:30560d0185ad4508519274be5520b3157b2ee77fa25a9eabdba8065c385790ef" },
            new PrintUser { Id = 2, Login = "press.manager", DisplayName = "Денис Макаров", Role = Roles.Manager, PasswordHash = "d8316b51e8f4a5894559eeedd1511a64:a8459eb58bbdbead6b7d5b8803ed75d2069e27728c7f7ba8eb1f892bb85420ce" },
            new PrintUser { Id = 3, Login = "press.operator", DisplayName = "Илья Крылов", Role = Roles.Printer, PasswordHash = "ed3348d64051cf1595d60bd76561b1b1:198b26af0ee333dd72f39d4d6acab953e8fdc2c249b0341c467657b3c11ec591" },
            new PrintUser { Id = 4, Login = "press.operator2", DisplayName = "Вера Лукина", Role = Roles.Printer, PasswordHash = "1564aa0268babe38cce9bd4631ef02ff:76490cc2e25bd5f126e400e4f9869b544430773f7b697a2f189df02edb55235d" });
        var order = model.Entity<PrintOrder>();
        order.HasIndex(row => new { row.Status, row.CreatedAt });
        order.HasIndex(row => new { row.PrinterId, row.CreatedAt });
        order.Property(row => row.Version).IsConcurrencyToken();
        var utc = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
            value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        order.Property(row => row.CreatedAt).HasConversion(utc);
        order.Property(row => row.CompletedAt).HasConversion(utc);
        model.Entity<Customer>().Property(row => row.RegisteredAt).HasConversion(utc);
        order.HasOne(row => row.Customer).WithMany().HasForeignKey(row => row.CustomerId).OnDelete(DeleteBehavior.Restrict);
        order.HasOne(row => row.Printer).WithMany().HasForeignKey(row => row.PrinterId).OnDelete(DeleteBehavior.Restrict);
    }
}
