using SheetVol.Core.Tests.Views;
using SheetVol.Core.Tracking;
using SheetVol.Core.Views;

namespace SheetVol.Core.Tests.Tracking;

/// <summary>Idempotence logic for E811 and T19, plus the removal rules of E308 and E612.</summary>
public class ChangeSetTests
{
    private static readonly Guid S1 = Guid.Parse("51515151-0000-0000-0000-000000000001");
    private static readonly Guid S2 = Guid.Parse("51515151-0000-0000-0000-000000000002");
    private static readonly Guid An = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");

    private static OwnedKey K(Guid seg, OwnedRole role, string q = "") => new(seg, role, q);

    private static (List<PlannedObject> Planned, List<StoredObject> Stored) Matching(params (OwnedKey Key, string Sig, string Handle)[] items)
        => (items.Select(i => new PlannedObject(i.Key, i.Sig)).ToList(),
            items.Select(i => new StoredObject(i.Key, i.Handle, true, i.Sig)).ToList());

    [Fact]
    public void E811_nothing_changed_writes_only_the_timestamp_and_keeps_every_handle()
    {
        var (planned, stored) = Matching(
            (K(S1, OwnedRole.Boundary), "b1", "1F0"),
            (K(S1, OwnedRole.Layout), "l1", "1F1"),
            (K(S1, OwnedRole.Viewport), "v1", "1F2"),
            (K(S1, OwnedRole.PaperTable, An.ToString("D")), "t1", "1F3"));

        ChangeSet cs = ChangeSetComparer.Compare(planned, stored, "rec", "rec");

        Assert.True(cs.OnlyTimestamp);
        Assert.Empty(cs.Writes);
        Assert.All(cs.Changes, c => Assert.Equal(ChangeKind.Unchanged, c.Kind));
        Assert.Equal(["1F0", "1F1", "1F2", "1F3"], cs.Changes.Select(c => c.Handle));
    }

    [Fact]
    public void A_changed_object_is_updated_in_place_with_its_handle()
    {
        var (planned, stored) = Matching((K(S1, OwnedRole.Viewport), "v1", "1F2"), (K(S1, OwnedRole.Boundary), "b1", "1F0"));
        planned[0] = planned[0] with { Signature = "v2" };

        ChangeSet cs = ChangeSetComparer.Compare(planned, stored, "rec", "rec");

        Assert.False(cs.OnlyTimestamp);
        ObjectChange w = Assert.Single(cs.Writes);
        Assert.Equal(new ObjectChange(K(S1, OwnedRole.Viewport), ChangeKind.Update, "1F2"), w);
    }

    [Fact]
    public void E802_an_erased_stored_object_is_recreated()
    {
        var planned = new List<PlannedObject> { new(K(S1, OwnedRole.PaperTable, "a"), "t1") };
        var stored = new List<StoredObject> { new(K(S1, OwnedRole.PaperTable, "a"), "2A0", Exists: false, null) };

        ChangeSet cs = ChangeSetComparer.Compare(planned, stored, "rec", "rec");

        Assert.Equal(ChangeKind.Create, Assert.Single(cs.Changes).Kind);
        Assert.False(cs.OnlyTimestamp);
    }

    [Fact]
    public void E308_E612_a_removed_segment_erases_its_model_objects_and_orphans_its_layout()
    {
        var (planned, stored) = Matching((K(S1, OwnedRole.Boundary), "b1", "1F0"));
        stored.AddRange(
        [
            new(K(S2, OwnedRole.Boundary), "2F0", true, "b2"),
            new(K(S2, OwnedRole.MatchMark, "start"), "2F1", true, "m2"),
            new(K(S2, OwnedRole.PaperTable, "a"), "2F2", true, "t2"),
            new(K(S2, OwnedRole.Layout), "2F3", true, "l2"),
            new(K(S2, OwnedRole.Viewport), "2F4", true, "v2"),
            new(K(S2, OwnedRole.Clip), "2F5", true, "c2"),
            new(K(S2, OwnedRole.ModelTable, "a"), "2F6", false, null),   // already gone: nothing to do
        ]);

        ChangeSet cs = ChangeSetComparer.Compare(planned, stored, "rec2", "rec");
        var byHandle = cs.Changes.Where(c => c.Key.SegmentId == S2).ToDictionary(c => c.Handle!, c => c.Kind);

        Assert.Equal(ChangeKind.Erase, byHandle["2F0"]);
        Assert.Equal(ChangeKind.Erase, byHandle["2F1"]);
        Assert.Equal(ChangeKind.Erase, byHandle["2F2"]);
        Assert.Equal(ChangeKind.Orphan, byHandle["2F3"]);
        Assert.Equal(ChangeKind.Orphan, byHandle["2F4"]);
        Assert.Equal(ChangeKind.Orphan, byHandle["2F5"]);
        Assert.False(byHandle.ContainsKey("2F6"));
        Assert.Equal(3, cs.Writes.Count());
    }

    [Fact]
    public void E608_a_locked_view_is_kept_even_when_it_differs_but_still_created_when_missing()
    {
        var planned = new List<PlannedObject>
        {
            new(K(S1, OwnedRole.Viewport), "v-new", Locked: true),
            new(K(S2, OwnedRole.Viewport), "v-new", Locked: true),
        };
        var stored = new List<StoredObject> { new(K(S1, OwnedRole.Viewport), "1F2", true, "v-old") };

        ChangeSet cs = ChangeSetComparer.Compare(planned, stored, "rec", "rec");

        Assert.Equal(new ObjectChange(K(S1, OwnedRole.Viewport), ChangeKind.Kept, "1F2"), cs.Changes[0]);
        Assert.Equal(ChangeKind.Create, cs.Changes[1].Kind);
    }

    [Fact]
    public void A_changed_record_is_more_than_a_timestamp_write()
    {
        var (planned, stored) = Matching((K(S1, OwnedRole.Boundary), "b1", "1F0"));
        Assert.False(ChangeSetComparer.Compare(planned, stored, "rec-new", "rec-old").OnlyTimestamp);
        Assert.False(ChangeSetComparer.Compare(planned, stored, "rec", null).OnlyTimestamp);
    }

    [Fact]
    public void Duplicate_keys_are_a_bug()
    {
        var dupPlanned = new List<PlannedObject> { new(K(S1, OwnedRole.Boundary), "a"), new(K(S1, OwnedRole.Boundary), "b") };
        Assert.Throws<ArgumentException>(() => ChangeSetComparer.Compare(dupPlanned, [], "r", "r"));
        var dupStored = new List<StoredObject> { new(K(S1, OwnedRole.Boundary), "1", true, "a"), new(K(S1, OwnedRole.Boundary), "2", true, "a") };
        Assert.Throws<ArgumentException>(() => ChangeSetComparer.Compare([], dupStored, "r", "r"));
    }

    [Fact]
    public void T19_regen_twice_changes_no_handles_and_no_geometry()
    {
        // T2's breaks on alignment S with offsets (-100, +100), FitStandard views, clip to boundary.
        var alignment = C3Alignment.Straight(new Pt(0, 0), 0, 2000);
        double[] breaks = [0, 500, 1300, 1800, 2000];
        var options = new ViewOptions(ScaleMode: ScaleMode.FitStandard, StandardScales: [20, 30, 40, 50, 60, 100], ViewOverlap: 20);
        Guid[] ids = Enumerable.Range(1, 4).Select(i => Guid.Parse($"7e7e7e7e-0000-0000-0000-00000000000{i}")).ToArray();

        List<PlannedObject> Plan()
        {
            var calc = new ViewCalculator(new ViewMathConfig());
            var src = new C3BandSource(alignment, -100, 100);
            var planned = new List<PlannedObject>();
            for (int i = 0; i < 4; i++)
            {
                var seg = new SegmentDef(ids[i], (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), breaks[i], breaks[i + 1], -100, 100);
                ViewResult view = calc.Compute(alignment, src, new ViewRequest(seg, options, 12, 5));
                Assert.True(view.Succeeded, string.Join(" | ", view.Problems.Select(p => p.Message)));
                planned.Add(new PlannedObject(K(seg.Id, OwnedRole.Boundary), ContentSignatures.OfPolyline(src.Build(seg.StartStation, seg.EndStation))));
                planned.Add(new PlannedObject(K(seg.Id, OwnedRole.Layout), ContentSignatures.OfTexts([$"Run1 - {i + 1}"])));
                planned.Add(new PlannedObject(K(seg.Id, OwnedRole.Viewport), ContentSignatures.Of(view.Spec!)));
            }
            return planned;
        }

        // First regen: nothing stored, everything is created; the writer records handles and live signatures.
        List<PlannedObject> first = Plan();
        ChangeSet initial = ChangeSetComparer.Compare(first, [], "rec", null);
        Assert.All(initial.Changes, c => Assert.Equal(ChangeKind.Create, c.Kind));
        var stored = first.Select((p, n) => new StoredObject(p.Key, (0x100 + n).ToString("X", System.Globalization.CultureInfo.InvariantCulture), true, p.Signature)).ToList();

        // Second regen from the same sources.
        ChangeSet second = ChangeSetComparer.Compare(Plan(), stored, "rec", "rec");

        Assert.True(second.OnlyTimestamp);
        Assert.Equal(stored.Select(s => s.Handle), second.Changes.Select(c => c.Handle));
        Assert.All(second.Changes, c => Assert.Equal(ChangeKind.Unchanged, c.Kind));
    }

    [Fact]
    public void Content_signatures_see_every_view_field()
    {
        var spec = new ViewSpec(S1, new Pt(250, 0), 0, 50, 12, 5, [new PolyVertex(new Pt(-5, -2), 0), new PolyVertex(new Pt(5, 2), 0.5)]);
        string baseline = ContentSignatures.Of(spec);
        Assert.Equal(baseline, ContentSignatures.Of(spec with { }));
        Assert.NotEqual(baseline, ContentSignatures.Of(spec with { ModelCenter = new Pt(250, 1e-9) }));
        Assert.NotEqual(baseline, ContentSignatures.Of(spec with { TwistRadians = 1e-12 }));
        Assert.NotEqual(baseline, ContentSignatures.Of(spec with { ModelUnitsPerPaperUnit = 40 }));
        Assert.NotEqual(baseline, ContentSignatures.Of(spec with { ClipInPaper = null }));
        Assert.NotEqual(baseline, ContentSignatures.Of(spec with { ClipInPaper = [new PolyVertex(new Pt(-5, -2), 0), new PolyVertex(new Pt(5, 2), 0.25)] }));
        Assert.NotEqual(ContentSignatures.OfTexts(["a", null]), ContentSignatures.OfTexts(["a", ""]));
    }
}
