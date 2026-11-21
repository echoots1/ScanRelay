using Microsoft.EntityFrameworkCore;
using ScanRelay.Core.Data;

namespace ScanRelay.Core.Tests;

public class SchemaTests
{
    private static readonly DateTime Now = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public void Migrations_match_the_model()
    {
        using var database = new TestDatabase();
        using var db = database.NewContext();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(db.Database.GetPendingMigrations());
    }

    [Fact]
    public void A_manual_person_can_be_placed_on_chosen_printers()
    {
        using var database = new TestDatabase();
        var personId = Guid.NewGuid();

        using (var db = database.NewContext())
        {
            var printer = new Printer { Name = "Front office", Address = "printer.example", ModuleId = "sample", CreatedUtc = Now };
            var person = new Person
            {
                Id = personId,
                DisplayName = "Test Person",
                UserPrincipalName = "test.person@example.com",
                Source = PersonSource.Manual,
                OnAllPrinters = false,
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            };
            person.Printers.Add(new PersonPrinter { Person = person, Printer = printer });
            db.Add(person);
            db.SaveChanges();
        }

        using (var db = database.NewContext())
        {
            var person = db.People.Include(p => p.Printers).ThenInclude(l => l.Printer).Single();
            Assert.Equal(personId, person.Id);
            Assert.Equal(PersonSource.Manual, person.Source);
            Assert.Equal("Front office", Assert.Single(person.Printers).Printer.Name);
        }
    }

    [Fact]
    public void Printer_names_are_unique()
    {
        using var database = new TestDatabase();
        using var db = database.NewContext();
        db.Add(new Printer { Name = "Same", Address = "a.example", ModuleId = "sample", CreatedUtc = Now });
        db.Add(new Printer { Name = "Same", Address = "b.example", ModuleId = "sample", CreatedUtc = Now });

        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void A_job_keeps_its_status_error_and_times()
    {
        using var database = new TestDatabase();
        var id = Guid.NewGuid();

        using (var db = database.NewContext())
        {
            db.Add(new Job
            {
                Id = id,
                FolderName = "not-a-user-id",
                OriginalFileName = "scan.pdf",
                SizeBytes = 123,
                Status = JobStatus.Failed,
                Error = "Folder matches no known person",
                Attempts = 1,
                DetectedUtc = Now,
                FinishedUtc = Now.AddSeconds(5),
            });
            db.SaveChanges();
        }

        using (var db = database.NewContext())
        {
            var job = db.Jobs.Single(j => j.Status == JobStatus.Failed && j.DetectedUtc >= Now);
            Assert.Equal(id, job.Id);
            Assert.Null(job.PersonId);
            Assert.Equal(Now.AddSeconds(5), job.FinishedUtc);
            Assert.Null(job.FileDeletedUtc);
        }
    }

    [Fact]
    public void A_run_records_each_push_and_what_the_printer_now_holds()
    {
        using var database = new TestDatabase();
        var personId = Guid.NewGuid();

        using (var db = database.NewContext())
        {
            var printer = new Printer { Name = "Front office", Address = "printer.example", ModuleId = "sample", CreatedUtc = Now };
            printer.Entries.Add(new PrinterEntry { PersonId = personId, DisplayName = "Test Person", PushedUtc = Now });
            var run = new Run { Kind = RunKind.Sync, Trigger = RunTrigger.Manual, Status = RunStatus.Held, StartedUtc = Now, Actor = "admin" };
            run.Pushes.Add(new Push { Printer = printer, Status = PushStatus.Failed, Error = "unreachable", StartedUtc = Now });
            db.Add(run);
            db.SaveChanges();
        }

        using (var db = database.NewContext())
        {
            var run = db.Runs.Include(r => r.Pushes).ThenInclude(p => p.Printer).ThenInclude(p => p.Entries).Single();
            Assert.Equal(RunStatus.Held, run.Status);
            var push = Assert.Single(run.Pushes);
            Assert.Equal(PushStatus.Failed, push.Status);
            Assert.Equal(personId, Assert.Single(push.Printer.Entries).PersonId);
        }
    }

    [Fact]
    public void Settings_and_audit_entries_round_trip()
    {
        using var database = new TestDatabase();

        using (var db = database.NewContext())
        {
            db.Add(new Setting { Key = "SyncIntervalMinutes", Value = "60", UpdatedUtc = Now });
            db.Add(new AuditEntry { AtUtc = Now, ActorName = "Setup", Action = "Setting changed", Target = "SyncIntervalMinutes" });
            db.SaveChanges();
        }

        using (var db = database.NewContext())
        {
            Assert.Equal("60", db.Settings.Single(s => s.Key == "SyncIntervalMinutes").Value);
            var entry = db.AuditLog.Single();
            Assert.Null(entry.ActorId);
            Assert.Equal(Now, entry.AtUtc);
        }
    }
}
