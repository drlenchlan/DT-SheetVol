// T22 (HANDOFF.md section 8): dialog view models against a fake ISheetVolSession,
// loaded with T2's breaks.

using SheetVol.Core.Presentation;

namespace SheetVol.Core.Tests.Presentation;

public class T22DialogTests
{
    [Fact]
    public async Task T22_T2_breaks_give_four_rows_end_edit_moves_next_begin_and_E201_errors_row_2_and_hides_totals()
    {
        var (session, _, vm) = Fixture.Open();
        var rows = vm.Grid.Rows;

        // Grid shows 4 rows with lengths 500, 800, 500, 200.
        Assert.Equal(4, rows.Count);
        Assert.Equal([1, 2, 3, 4], rows.Select(r => r.Number));
        Assert.Equal([500d, 800d, 500d, 200d], rows.Select(r => r.Length));
        Assert.Equal(["500.00", "800.00", "500.00", "200.00"], rows.Select(r => r.LengthText));

        await vm.RegenerateCommand.ExecuteAsync();
        Assert.True(vm.Grid.TotalsVisible);
        Assert.All(rows, r => Assert.Equal(SegmentStatus.Current, r.Status));

        // Editing row 2's end to 12+00 moves row 3's begin.
        rows[1].EndStation.Text = "12+00";
        Assert.False(rows[1].EndStation.HasError);
        Assert.Equal(1200, rows[1].EndRaw);
        Assert.Equal(1200, rows[2].BeginRaw);
        Assert.Equal(Fixture.Sta(session, 1200), rows[1].EndStation.Text);
        Assert.Equal(Fixture.Sta(session, 1200), rows[2].BeginStation.Text);
        Assert.Equal([500d, 700d, 600d, 200d], rows.Select(r => r.Length));

        // An offset wider than the curve radius puts row 2 in Error with the E201
        // message from the session's validation, and hides totals.
        vm.OffsetRight.Text = "350";
        var e201 = Assert.Single(vm.Validation!.Problems, p => p.Code == "SV-E201");
        Assert.Equal(SegmentStatus.Error, rows[1].Status);
        Assert.Equal(DialogText.StatusError, rows[1].StatusText);
        Assert.True(rows[1].IsError);
        Assert.Equal("SV-E201", rows[1].ProblemCode);
        Assert.Equal(e201.Message, rows[1].Message);
        Assert.Equal(e201.Fix, rows[1].Fix);
        Assert.False(rows[1].HasValues);
        Assert.False(vm.Grid.TotalsVisible);
        Assert.Equal(DialogText.TotalsPending, vm.Grid.ReconciliationText);
        Assert.False(vm.CanRegenerate);

        // Only the one explicit regen ever reached the session.
        Assert.Equal(1, session.RegenerateCalls);
    }
}
