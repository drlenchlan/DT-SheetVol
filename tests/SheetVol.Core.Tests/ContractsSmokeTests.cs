// Wave 0 smoke: the test host runs and the frozen contracts construct. Real
// suites arrive with C1 to C5 (HANDOFF.md section 10).

namespace SheetVol.Core.Tests;

public class ContractsSmokeTests
{
    [Fact]
    public void SegmentDef_holds_a_signed_band()
    {
        var seg = new SegmentDef(Guid.NewGuid(), "1", 0, 500, -100, 100);
        Assert.True(seg.OffsetLeft < seg.OffsetRight);
        Assert.Equal(200, seg.OffsetRight - seg.OffsetLeft);
    }

    [Fact]
    public void RunDefinition_defaults_match_section_3()
    {
        var run = new RunDefinition(
            "Run1", "ALIGN-1", SegmentDriver.Stations, "START..END@500", null,
            -100, 100, [], [], [], new Dictionary<Guid, SegmentOverrides>(), new ViewOptions());

        Assert.Equal(AutoMode.Views, run.AutoMode);            // A4
        Assert.Equal(ReportPreset.Standard, run.Preset);
        Assert.Equal(AreaUnit.SquareFeet, run.AreaUnit);       // 6.5 default
        Assert.Equal(ViewOrientation.AlongAlignment, run.Views.Orientation);   // A3
    }
}
