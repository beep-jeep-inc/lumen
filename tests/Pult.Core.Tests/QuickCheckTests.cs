using Pult.Core.Maintenance;
using Xunit;

namespace Pult.Core.Tests;

public class QuickCheckTests
{
    private sealed class Step(string title, Func<Task>? run = null) : IQuickStep
    {
        public string Title { get; } = title;
        public bool NeedsAction { get; set; }
        public bool NeedsManual { get; set; }
        public int Runs { get; private set; }
        public int Cancels { get; private set; }
        public List<string>? Log { get; set; }
        public async Task RunAsync() { Runs++; Log?.Add(Title); if (run != null) await run(); }
        public void Cancel() => Cancels++;
    }

    [Fact]
    public void Plan_has_only_light_checks_in_order_and_never_heavy_ones()
    {
        Assert.Equal(new[] { "startup", "disks", "libraries" }, QuickCheckPlan.Order);
        foreach (var heavy in new[] { "drivers", "programs", "cleanup", "space", "duplicates", "repair", "disks-details" })
            Assert.False(QuickCheckPlan.IsQuick(heavy), heavy);
    }

    [Fact]
    public async Task Runs_steps_one_by_one_in_order()
    {
        var log = new List<string>();
        var running = 0; var maxParallel = 0;
        Func<Task> work = async () => { running++; maxParallel = Math.Max(maxParallel, running); await Task.Yield(); running--; };
        var steps = new[] { new Step("A", work) { Log = log }, new Step("B", work) { Log = log }, new Step("C", work) { Log = log } };
        var r = new QuickCheckRunner(steps, () => new DateTime(2026, 10, 2, 14, 5, 0));
        await r.RunAsync();
        Assert.Equal(new[] { "A", "B", "C" }, log);
        Assert.Equal(1, maxParallel);
        Assert.False(r.Running);
        Assert.Equal(3, r.Summary!.Done);
    }

    [Fact]
    public async Task Progress_text_names_current_step_and_position()
    {
        var seen = new List<string>();
        QuickCheckRunner? r = null;
        var steps = new[] { new Step("Автозагрузка", () => { seen.Add(r!.ProgressText); return Task.CompletedTask; }), new Step("Здоровье дисков", () => { seen.Add(r!.ProgressText); return Task.CompletedTask; }) };
        r = new QuickCheckRunner(steps);
        await r.RunAsync();
        Assert.Equal(new[] { "Проверяю: Автозагрузка · 1 из 2", "Проверяю: Здоровье дисков · 2 из 2" }, seen);
        Assert.Equal("", r.ProgressText);
    }

    [Fact]
    public async Task Pending_steps_are_queued_until_their_turn()
    {
        var states = new List<bool>();
        QuickCheckRunner? r = null;
        var b = new Step("B");
        var a = new Step("A", () => { states.Add(r!.IsQueued(b)); return Task.CompletedTask; });
        r = new QuickCheckRunner(new[] { a, b });
        await r.RunAsync();
        Assert.Equal(new[] { true }, states);
        Assert.False(r.IsQueued(b));
    }

    [Fact]
    public async Task Cancel_stops_current_step_and_skips_the_rest()
    {
        var gate = new TaskCompletionSource();
        QuickCheckRunner? r = null;
        var a = new Step("A");
        var b = new Step("B", () => gate.Task);
        var c = new Step("C");
        r = new QuickCheckRunner(new[] { a, b, c });
        var run = r.RunAsync();
        while (r.Current != b) await Task.Delay(1);
        r.Cancel();
        Assert.Equal(1, b.Cancels);
        gate.SetResult();
        await run;
        Assert.Equal(0, c.Runs);
        Assert.True(r.Summary!.Cancelled);
        Assert.Equal(1, r.Summary.Done);                                // прерванный шаг проверенным не считается
        Assert.Equal("Быстрая проверка остановлена · проверено 1 из 3", r.Summary.Text);
    }

    [Fact]
    public async Task Summary_counts_sections_with_something_to_do()
    {
        var steps = new[] { new Step("A") { NeedsAction = true }, new Step("B"), new Step("C") { NeedsAction = true } };
        var r = new QuickCheckRunner(steps, () => new DateTime(2026, 10, 2, 9, 7, 0));
        await r.RunAsync();
        Assert.Equal("Быстрая проверка в 09:07 · есть что сделать в 2 разделах", r.Summary!.Text);
    }

    [Fact]
    public async Task Summary_all_fine_and_single_section()
    {
        var r = new QuickCheckRunner(new[] { new Step("A"), new Step("B") }, () => new DateTime(2026, 10, 2, 14, 32, 0));
        await r.RunAsync();
        Assert.Equal("Быстрая проверка в 14:32 · всё в порядке", r.Summary!.Text);
        var r1 = new QuickCheckRunner(new[] { new Step("A") { NeedsAction = true } }, () => new DateTime(2026, 10, 2, 14, 32, 0));
        await r1.RunAsync();
        Assert.Equal("Быстрая проверка в 14:32 · есть что сделать в 1 разделе", r1.Summary!.Text);
    }

    [Theory]
    [InlineData(1, "разделе")] [InlineData(2, "разделах")] [InlineData(4, "разделах")] [InlineData(5, "разделах")]
    [InlineData(11, "разделах")] [InlineData(21, "разделе")] [InlineData(12, "разделах")]
    public void Plural_form(int n, string word) => Assert.Equal(word, QuickSummary.Sections(n));

    [Fact]
    public async Task Failed_step_does_not_stop_the_queue_and_is_reported()
    {
        var bad = new Step("A", () => throw new InvalidOperationException("boom"));
        var ok = new Step("B") { NeedsAction = true };
        var r = new QuickCheckRunner(new[] { bad, ok });
        await r.RunAsync();
        Assert.Equal(1, ok.Runs);
        Assert.Equal(1, r.Summary!.Manual);
        Assert.Contains("не удалось проверить: 1", r.Summary.Text);
    }

    [Fact]
    public async Task Step_marked_manual_is_counted_and_mentioned()
    {
        var r = new QuickCheckRunner(new[] { new Step("A") { NeedsManual = true }, new Step("B") });
        await r.RunAsync();
        Assert.Equal(1, r.Summary!.Manual);
        Assert.Contains("не удалось проверить: 1", r.Summary.Text);
    }

    [Fact]
    public async Task Second_start_while_running_is_ignored_and_runner_can_be_reused()
    {
        var gate = new TaskCompletionSource();
        var a = new Step("A", () => gate.Task);
        var r = new QuickCheckRunner(new[] { a });
        var first = r.RunAsync();
        await r.RunAsync();                                              // идёт — второй запуск ничего не делает
        gate.SetResult(); await first;
        Assert.Equal(1, a.Runs);
        await r.RunAsync();
        Assert.Equal(2, a.Runs);
    }

    [Fact]
    public async Task Quiet_mode_blocks_windows_and_reports_hit()
    {
        Assert.False(QuietMode.Blocks());                                // вне быстрой проверки окна разрешены
        QuietMode.Begin();
        var shown = !QuietMode.Blocks();                                 // шаг пытается показать окно — получает отказ
        var hit = QuietMode.End();
        Assert.False(shown);
        Assert.True(hit);
        Assert.False(QuietMode.Active);
        QuietMode.Begin();
        Assert.False(QuietMode.End());                                   // окон не было — отметки нет
        await Task.CompletedTask;
    }
}
