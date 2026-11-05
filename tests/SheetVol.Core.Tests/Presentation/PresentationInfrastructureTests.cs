// The small building blocks: number display, commands, text field, pending edits.

using System.ComponentModel;
using System.Globalization;
using SheetVol.Core.Presentation;

namespace SheetVol.Core.Tests.Presentation;

public class PresentationInfrastructureTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Fact]
    public void NumberDisplay_rounds_half_away_from_zero_groups_thousands_and_never_shows_negative_zero()
    {
        Assert.Equal("0", new NumberDisplay(0).Format(-0.4, Inv));
        Assert.Equal("3", new NumberDisplay(0).Format(2.5, Inv));
        Assert.Equal("-3", new NumberDisplay(0).Format(-2.5, Inv));
        Assert.Equal("0.13", new NumberDisplay(2).Format(0.125, Inv));
        Assert.Equal("160,000", new NumberDisplay(0).Format(160_000, Inv));
        Assert.Equal("1.25", new NumberDisplay(4, Grouped: false, TrimZeros: true).Format(1.25, Inv));
        Assert.Equal("1234,50", new NumberDisplay(2, Grouped: false).Format(1234.5, CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void RelayCommand_respects_can_execute_and_raises_changes()
    {
        var allowed = false;
        var runs = 0;
        var raised = 0;
        var command = new RelayCommand(() => runs++, () => allowed);
        command.CanExecuteChanged += (_, _) => raised++;

        command.Execute();
        allowed = true;
        command.RaiseCanExecuteChanged();
        command.Execute();

        Assert.Equal(1, runs);
        Assert.Equal(1, raised);

        var typed = new RelayCommand<Guid>(_ => runs++);
        Assert.False(typed.CanExecute("not a guid"));
        Assert.True(typed.CanExecute(Guid.Empty));
    }

    [Fact]
    public async Task AsyncRelayCommand_is_not_reentrant()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var command = new AsyncRelayCommand(async () => { runs++; await gate.Task; });

        var first = command.ExecuteAsync();
        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute());
        await command.ExecuteAsync();                             // ignored while running
        gate.SetResult();
        await first;

        Assert.Equal(1, runs);
        Assert.True(command.CanExecute());
    }

    [Fact]
    public void TextField_keeps_typed_text_on_error_and_revert_restores_the_last_valid_text()
    {
        var committed = new List<string>();
        var field = new TextFieldViewModel("Offset", t => { committed.Add(t); return t == "bad" ? "Type a number." : null; });
        var errors = 0;
        field.ErrorsChanged += (_, _) => errors++;

        field.Text = "12";
        field.Text = "bad";

        Assert.Equal(["12", "bad"], committed);
        Assert.Equal("bad", field.Text);
        Assert.True(field.HasErrors);
        Assert.Equal(["Type a number."], field.GetErrors(null).Cast<string>());
        Assert.Equal(1, errors);

        field.Revert();
        Assert.Equal("12", field.Text);
        Assert.False(field.HasError);
        Assert.Equal(2, committed.Count);                          // revert commits nothing
    }

    [Fact]
    public void ObservableObject_raises_only_when_a_value_changes()
    {
        var field = new TextFieldViewModel("x", _ => null);
        var names = new List<string?>();
        ((INotifyPropertyChanged)field).PropertyChanged += (_, e) => names.Add(e.PropertyName);

        field.Text = "a";
        field.Text = "a";

        Assert.Equal([nameof(TextFieldViewModel.Text)], names);
    }

    [Fact]
    public void PendingEdits_are_keyed_by_drawing_name_ignoring_case()
    {
        var store = new PendingEdits();
        var edit = new PendingEdit(Fixture.T2Run() with { Draw = new DrawOptions(true, false, true) });

        store.Save(@"C:\proj\A.dwg", edit);

        Assert.True(store.TryGet(@"c:\PROJ\a.DWG", out var found));
        Assert.Same(edit, found);
        Assert.False(store.TryGet(@"C:\proj\B.dwg", out _));
        Assert.True(store.Clear(@"C:\proj\A.dwg"));
        Assert.Equal(0, store.Count);
    }
}
