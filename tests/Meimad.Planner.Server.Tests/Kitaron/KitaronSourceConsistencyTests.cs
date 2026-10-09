using System.Data;
using Meimad.Planner.Server.Application.Kitaron;

namespace Meimad.Planner.Server.Tests.Kitaron;

/// <summary>
/// The ERP database decides whether the import reads under SNAPSHOT isolation; Planner never changes
/// ERP settings, so a database without it is read in one READ COMMITTED transaction and the read mode
/// is recorded as evidence (owner decision 2026-10-09: 0.1.190 refused every import on the factory
/// database, which has no snapshot isolation).
/// </summary>
public sealed class KitaronSourceConsistencyTests
{
    [Fact]
    public void Snapshot_isolation_is_used_only_when_the_database_reports_it_on()
    {
        var (isolation, mode, caveat) = SqlServerKitaronSourceReader.ChooseIsolation(1);
        Assert.Equal(IsolationLevel.Snapshot, isolation);
        Assert.Equal(SqlServerKitaronSourceReader.SnapshotMode, mode);
        Assert.Null(caveat);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(null)]
    public void A_database_without_snapshot_isolation_is_read_committed_and_the_read_says_so(int? state)
    {
        var (isolation, mode, caveat) = SqlServerKitaronSourceReader.ChooseIsolation(state is null ? DBNull.Value : state.Value);
        Assert.Equal(IsolationLevel.ReadCommitted, isolation);
        Assert.Equal(SqlServerKitaronSourceReader.ReadCommittedMode, mode);
        Assert.Contains("no SNAPSHOT isolation", caveat);
        Assert.Contains("does not change ERP database settings", caveat);
    }

    [Fact]
    public void View_inspection_records_a_caveat_instead_of_refusing_the_import()
    {
        Assert.Null(SqlServerKitaronSourceReader.DefinitionCaveat("CREATE VIEW v AS SELECT 1 FROM dbo.TOrder", 0, 0));
        Assert.Contains("dirty-read hint", SqlServerKitaronSourceReader.DefinitionCaveat("SELECT * FROM dbo.TOrder WITH (NOLOCK)", 0, 0));
        Assert.Contains("VIEW DEFINITION", SqlServerKitaronSourceReader.DefinitionCaveat(null, 1, 0));
        Assert.Contains("external", SqlServerKitaronSourceReader.DefinitionCaveat("SELECT 1", 0, 1));
        Assert.Contains("nested", SqlServerKitaronSourceReader.DefinitionCaveat("SELECT 1", 32, 0));
    }

    [Fact]
    public void The_read_mode_and_caveat_travel_with_the_source_consistency()
    {
        var consistency = new KitaronSourceConsistency(SqlServerKitaronSourceReader.ReadCommittedMode,
            DateTimeOffset.Parse("2026-10-09T06:00:00Z"), DateTimeOffset.Parse("2026-10-09T06:00:02Z"), "caveat");
        Assert.Equal("caveat", consistency.Caveat);
        Assert.Null(new KitaronSourceConsistency("x", consistency.StartedAt, consistency.CompletedAt).Caveat);
    }
}
