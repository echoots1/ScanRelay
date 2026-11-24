using ScanRelay.Core.Printers;
using ScanRelay.Core.Sync;

namespace ScanRelay.Core.Tests;

public sealed class DriftCompareTests
{
    private static readonly Guid Id = Guid.NewGuid();

    [Theory]
    [InlineData("Ann Example", "ann@example.com", "Ann Example", "ann@example.com", true)]
    [InlineData("Bartholomew Feat", null, "Bartholomew Featherstonehaugh", "b@example.com", true)]   // cut by the printer, and no email on its entries
    [InlineData("Ann Example", "old@example.com", "Ann Example", "ann@example.com", false)]
    [InlineData("Ann", null, "Anna Example", null, false)]                                            // too short to be a cut name
    [InlineData("Bob Example", null, "Ann Example", null, false)]
    public void A_cut_name_or_a_missing_email_is_not_a_difference(string heldName, string? heldEmail, string wantedName, string? wantedEmail, bool same) =>
        Assert.Equal(same, DirectorySync.SameOnPrinter(new WantedEntry(Id, heldName, heldEmail), new WantedEntry(Id, wantedName, wantedEmail)));

    [Fact]
    public void Another_person_is_always_a_difference() =>
        Assert.False(DirectorySync.SameOnPrinter(new WantedEntry(Guid.NewGuid(), "Ann Example"), new WantedEntry(Id, "Ann Example")));
}
