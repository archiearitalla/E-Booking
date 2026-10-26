using System.Text.RegularExpressions;
using VeskaFlow.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<Staff> StaffMembers => Set<Staff>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<StaffService> StaffServices => Set<StaffService>();
    public DbSet<WorkingHour> WorkingHours => Set<WorkingHour>();
    public DbSet<BusinessHour> BusinessHours => Set<BusinessHour>();
    public DbSet<BusinessLogo> BusinessLogos => Set<BusinessLogo>();
    public DbSet<Blackout> Blackouts => Set<Blackout>();
    public DbSet<WeeklyClosure> WeeklyClosures => Set<WeeklyClosure>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<StaffService>().HasKey(x => new { x.StaffId, x.ServiceId });

        // Relationships are declared so EF inserts parents before children. The database owns the
        // cascade rules (see 001_init.sql), so EF is told not to cascade on its own.
        m.Entity<AdminUser>().HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.NoAction);
        m.Entity<Staff>().HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.NoAction);
        m.Entity<Service>().HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.NoAction);
        m.Entity<Blackout>().HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.NoAction);
        m.Entity<Customer>().HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.NoAction);
        m.Entity<Booking>().HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.NoAction);
        m.Entity<AuditLog>().HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.NoAction);
        m.Entity<AuditLog>().HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.NoAction);
        m.Entity<StaffService>().HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Staff>().HasMany(s => s.StaffServices).WithOne().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Staff>().HasMany(s => s.WorkingHours).WithOne().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Blackout>().HasOne<Staff>().WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.NoAction);
        m.Entity<BusinessHour>().HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.NoAction);
        m.Entity<BusinessLogo>().HasKey(x => x.BusinessId);
        m.Entity<BusinessLogo>().HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.NoAction);
        m.Entity<WeeklyClosure>().HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.NoAction);
        m.Entity<WeeklyClosure>().HasOne<Staff>().WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.NoAction);

        m.Entity<Booking>().Property(b => b.Status)
            .HasConversion(v => v.ToDb(), v => BookingStatusExtensions.FromDb(v));

        // Optimistic concurrency via PostgreSQL's xmin system column: a stale edit throws DbUpdateConcurrencyException.
        m.Entity<Booking>().Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        // The generated column booking.active_range is deliberately NOT mapped: only the database writes it.

        foreach (var entity in m.Model.GetEntityTypes())
        {
            entity.SetTableName(ToSnake(entity.ClrType.Name));
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnake(property.Name));
        }
    }

    private static string ToSnake(string name) =>
        Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
}
