using SheetVol.Core.Views;

namespace SheetVol.Core.Tests.Views;

/// <summary>Layout names: E603 sanitize and de-duplicate, E307 two-pass renumbering, pattern formatting.</summary>
public class LayoutNameTests
{
    private static readonly LayoutNamer Namer = new(new LayoutNameConfig());
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid A2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    [Fact]
    public void E603_invalid_characters_are_replaced_and_the_final_name_is_reported()
    {
        var plan = Namer.PlanRenames([new OwnedLayout(A, null, "STA 0+00 / 5+00: \"EG\" <FG>")], []);

        LayoutNameAssignment a = Assert.Single(plan.Assignments);
        Assert.Equal("STA 0+00 _ 5+00_ _EG_ _FG_", a.Final);
        Assert.True(a.Sanitized);
        Assert.Equal(new LayoutCreate(A, a.Final), Assert.Single(plan.Creates));
        Problem p = Assert.Single(plan.Problems);
        Assert.Equal("SV-E603", p.Code);
        Assert.Equal(Severity.Info, p.Severity);
        Assert.Contains(a.Final, p.Message);
        Assert.Equal(A, p.SegmentId);
    }

    [Fact]
    public void E603_every_AutoCAD_rejected_character_and_control_characters_are_replaced()
    {
        SanitizedName s = Namer.Sanitize("a<b>c/d\\e\"f:g;h?i*j|k,l=m`n\to");
        Assert.Equal("a_b_c_d_e_f_g_h_i_j_k_l_m_n_o", s.Name);
        Assert.True(s.Sanitized);
        Assert.False(s.Truncated);
    }

    [Fact]
    public void E603_long_names_are_truncated_to_the_configured_limit()
    {
        var namer = new LayoutNamer(new LayoutNameConfig { MaxLayoutNameLength = 10 });
        SanitizedName s = namer.Sanitize("Sheet 0123456789");
        Assert.Equal("Sheet 0123", s.Name);
        Assert.True(s.Truncated);

        // The default limit is AutoCAD's 255.
        Assert.Equal(255, Namer.Sanitize(new string('x', 300)).Name.Length);
    }

    [Fact]
    public void E603_truncation_never_splits_a_surrogate_pair()
    {
        var namer = new LayoutNamer(new LayoutNameConfig { MaxLayoutNameLength = 4, DuplicateSuffixFormat = "{0}", TemporaryNameFormat = "~{0}" });
        string name = namer.Sanitize("abc\U0001F600z").Name;   // the emoji is two UTF-16 units at positions 3 and 4
        Assert.Equal("abc", name);
    }

    [Fact]
    public void E603_collisions_are_case_insensitive_and_get_a_deterministic_numeric_suffix()
    {
        string[] requested = ["Sheet 1", "sheet 1", "Sheet 1", "Other"];
        string[] occupied = ["SHEET 1 (2)"];

        var first = Namer.Assign(requested, occupied);
        var second = Namer.Assign(requested, occupied);

        Assert.Equal(["Sheet 1", "sheet 1 (3)", "Sheet 1 (4)", "Other"], first.Select(a => a.Final));
        Assert.Equal(first, second);
        Assert.False(first[0].Deduplicated);
        Assert.True(first[1].Deduplicated);
    }

    [Fact]
    public void E603_reserved_model_name_is_never_produced()
    {
        var a = Namer.Assign(["model", "Model"], []);
        Assert.Equal(["model (2)", "Model (3)"], a.Select(x => x.Final));
    }

    [Fact]
    public void E603_suffix_respects_the_length_limit()
    {
        var namer = new LayoutNamer(new LayoutNameConfig { MaxLayoutNameLength = 8 });
        var a = namer.Assign(["ABCDEFGH", "ABCDEFGH"], []);
        Assert.Equal("ABCDEFGH", a[0].Final);
        Assert.Equal("ABCD (2)", a[1].Final);
        Assert.All(a, x => Assert.True(x.Final.Length <= 8));
    }

    [Fact]
    public void E603_empty_or_blank_names_fall_back()
    {
        Assert.Equal("Layout", Namer.Sanitize("   ").Name);
        Assert.Equal("Layout", Namer.Sanitize(null).Name);
        Assert.Equal("Layout (2)", Namer.Assign(["", ""], [])[1].Final);
    }

    [Fact]
    public void E307_renumbering_uses_a_two_pass_rename_with_no_collision_at_any_step()
    {
        // Run R had sheets A, B, C named R - 1..3. A break inserted in A adds A2 after it,
        // so B and C renumber to 3 and 4 and A2 takes 2. A direct B -> "R - 3" would collide with C.
        string[] foreign = ["Layout1", "R - 9"];
        OwnedLayout[] owned =
        [
            new(A, "R - 1", "R - 1"),
            new(A2, null, "R - 2"),
            new(B, "R - 2", "R - 3"),
            new(C, "R - 3", "R - 4"),
        ];

        LayoutRenamePlan plan = Namer.PlanRenames(owned, foreign);

        Assert.Equal(2, plan.FirstPass.Count);
        Assert.Equal(2, plan.SecondPass.Count);
        Assert.Equal([new LayoutCreate(A2, "R - 2")], plan.Creates);
        Assert.DoesNotContain(plan.FirstPass, r => r.SegmentId == A);   // unchanged name, no step

        var names = new HashSet<string>(["Layout1", "R - 9", "R - 1", "R - 2", "R - 3"], StringComparer.OrdinalIgnoreCase);
        foreach (LayoutRename r in plan.FirstPass.Concat(plan.SecondPass))
        {
            Assert.Contains(r.From, names);
            Assert.DoesNotContain(r.To, names);
            names.Remove(r.From);
            names.Add(r.To);
        }
        foreach (LayoutCreate c in plan.Creates)
        {
            Assert.DoesNotContain(c.Name, names);
            names.Add(c.Name);
        }
        Assert.True(names.SetEquals(["Layout1", "R - 9", "R - 1", "R - 2", "R - 3", "R - 4"]));
        Assert.Equal(["R - 1", "R - 2", "R - 3", "R - 4"], plan.Assignments.Select(a => a.Final));
    }

    [Fact]
    public void E307_temporary_names_avoid_every_existing_and_final_name()
    {
        string[] foreign = ["SV-TMP-1", "sv-tmp-2"];
        var plan = Namer.PlanRenames([new OwnedLayout(A, "X", "Y"), new OwnedLayout(B, "Y", "X")], foreign);

        Assert.Equal(["SV-TMP-3", "SV-TMP-4"], plan.FirstPass.Select(r => r.To));
        Assert.Equal([new LayoutRename(A, "SV-TMP-3", "Y"), new LayoutRename(B, "SV-TMP-4", "X")], plan.SecondPass);
    }

    [Fact]
    public void Unchanged_names_give_an_empty_plan()
    {
        var plan = Namer.PlanRenames([new OwnedLayout(A, "R - 1", "R - 1"), new OwnedLayout(B, "R - 2", "R - 2")], ["Layout1"]);
        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void Requested_name_taken_by_a_foreign_layout_is_deduplicated_and_reported()
    {
        var plan = Namer.PlanRenames([new OwnedLayout(A, "R - 1", "Layout1")], ["Layout1"]);
        Assert.Equal("Layout1 (2)", plan.Assignments[0].Final);
        Assert.Equal([new LayoutRename(A, "R - 1", "SV-TMP-1")], plan.FirstPass);
        Assert.Contains("taken", Assert.Single(plan.Problems).Message);
    }

    [Fact]
    public void D20_E603_names_the_layout_holding_the_clean_name_and_points_at_SVPURGE()
    {
        // An orphan "r - 2" keeps its name; the new sheet requesting "R - 2" gets the suffix.
        var plan = Namer.PlanRenames([new OwnedLayout(A, null, "R - 2")], ["Layout1", "r - 2"]);

        Assert.Equal("R - 2 (2)", plan.Assignments[0].Final);
        Problem p = Assert.Single(plan.Problems);
        Assert.Equal("SV-E603", p.Code);
        Assert.Equal("Layout 'R - 2' is named 'R - 2 (2)': the name is taken by layout 'r - 2', which keeps it, so a number was added. If 'r - 2' is an orphan, SVPURGE removes orphans.", p.Message);
        Assert.EndsWith("SVPURGE removes orphans.", p.Message);

        // Two sheets of the same run colliding: no layout outside the run holds the name, no SVPURGE pointer.
        var inRun = Namer.PlanRenames([new OwnedLayout(A, null, "S"), new OwnedLayout(B, null, "S")], []);
        Assert.DoesNotContain("SVPURGE", Assert.Single(inRun.Problems).Message);
    }

    [Fact]
    public void Owned_names_must_be_unique_and_not_foreign()
    {
        Assert.Throws<ArgumentException>(() => Namer.PlanRenames([new OwnedLayout(A, "X", "X"), new OwnedLayout(B, "x", "Y")], []));
        Assert.Throws<ArgumentException>(() => Namer.PlanRenames([new OwnedLayout(A, "X", "X")], ["X"]));
    }

    [Fact]
    public void Pattern_tokens_are_replaced_and_unknown_tokens_fail()
    {
        var parts = new LayoutNameParts("EARTHWORK", 7, "7", "30+00", "35+00", "CL-MAIN");
        Assert.Equal("EARTHWORK - 7", Namer.FormatName(parts));
        Assert.Equal("{CL-MAIN} 007 30+00 to 35+00 #7",
            LayoutNamer.FormatPattern("{{{align}}} {N} {FROM} to {TO} #{LABEL}", parts, "000"));

        var ex = Assert.Throws<FormatException>(() => LayoutNamer.FormatPattern("{RUN}-{SHEET}", parts, "0"));
        Assert.Contains("{SHEET}", ex.Message);
        Assert.Throws<FormatException>(() => LayoutNamer.FormatPattern("{RUN", parts, "0"));
        Assert.Throws<FormatException>(() => LayoutNamer.FormatPattern("RUN}", parts, "0"));
    }

    [Fact]
    public void Invalid_config_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new LayoutNamer(new LayoutNameConfig { Replacement = "?" }));
        Assert.Throws<ArgumentException>(() => new LayoutNamer(new LayoutNameConfig { DuplicateSuffixFormat = " (n)" }));
        Assert.Throws<ArgumentException>(() => new LayoutNamer(new LayoutNameConfig { TemporaryNameFormat = "tmp:{0}" }));
        Assert.Throws<ArgumentException>(() => new LayoutNamer(new LayoutNameConfig { MaxLayoutNameLength = 3 }));
        Assert.Throws<ArgumentException>(() => new LayoutNamer(new LayoutNameConfig { EmptyNameFallback = "" }));
    }
}
