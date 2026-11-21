using Microsoft.EntityFrameworkCore;

namespace ScanRelay.Core.Data;

public class ScanRelayDbContext(DbContextOptions<ScanRelayDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<PersonPrinter> PersonPrinters => Set<PersonPrinter>();
    public DbSet<Printer> Printers => Set<Printer>();
    public DbSet<PrinterEntry> PrinterEntries => Set<PrinterEntry>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<Push> Pushes => Set<Push>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Setting>().HasKey(s => s.Key);

        modelBuilder.Entity<Person>(person =>
        {
            person.Property(p => p.Id).ValueGeneratedNever();
            person.Property(p => p.Source).HasConversion<string>();
        });

        modelBuilder.Entity<PersonPrinter>(link =>
        {
            link.HasKey(l => new { l.PersonId, l.PrinterId });
            link.HasOne(l => l.Person).WithMany(p => p.Printers).HasForeignKey(l => l.PersonId);
            link.HasOne(l => l.Printer).WithMany().HasForeignKey(l => l.PrinterId);
        });

        modelBuilder.Entity<Printer>().HasIndex(p => p.Name).IsUnique();

        modelBuilder.Entity<PrinterEntry>(entry =>
        {
            entry.HasKey(e => new { e.PrinterId, e.PersonId });
            entry.HasOne(e => e.Printer).WithMany(p => p.Entries).HasForeignKey(e => e.PrinterId);
        });

        modelBuilder.Entity<Job>(job =>
        {
            job.Property(j => j.Id).ValueGeneratedNever();
            job.Property(j => j.Status).HasConversion<string>();
            job.HasIndex(j => j.Status);
            job.HasIndex(j => j.DetectedUtc);
            job.HasIndex(j => j.PersonId);
        });

        modelBuilder.Entity<Run>(run =>
        {
            run.Property(r => r.Kind).HasConversion<string>();
            run.Property(r => r.Trigger).HasConversion<string>();
            run.Property(r => r.Status).HasConversion<string>();
            run.HasIndex(r => r.StartedUtc);
        });

        modelBuilder.Entity<Push>(push =>
        {
            push.Property(p => p.Status).HasConversion<string>();
            push.HasOne(p => p.Run).WithMany(r => r.Pushes).HasForeignKey(p => p.RunId);
            push.HasOne(p => p.Printer).WithMany().HasForeignKey(p => p.PrinterId);
        });

        modelBuilder.Entity<AuditEntry>().HasIndex(a => a.AtUtc);
    }
}
