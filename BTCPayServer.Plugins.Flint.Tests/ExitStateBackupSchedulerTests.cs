using BTCPayServer.Plugins.Flint.Services;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The cadence rules of the automatic exit-state backup: a coalescing debounce, a one-hour safety net,
/// and a content hash that keeps an unchanged state from being rewritten.
/// </summary>
/// <remarks>
/// <para>
/// Pure scheduler, no harness: every decision takes its <c>now</c> as an argument, and the one clock the
/// scheduler reads itself — the moment <see cref="ExitStateBackupScheduler.RequestRefresh"/> records — is
/// exposed by <c>PendingSince</c> precisely so these assertions run against the value the decisions
/// actually use rather than a second reading of the wall.
/// </para>
/// <para>
/// These are the decisions the whole feature is made of; the IO and the wiring are covered where they
/// live, in <c>FileExitStateBackupStoreTests</c> and <c>SparkExitStateAutoBackupTests</c>.
/// </para>
/// </remarks>
public class ExitStateBackupSchedulerTests
{
    private const string Store = "store-1";

    private static readonly DateTimeOffset Base = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan Min(int n) => TimeSpan.FromMinutes(n);

    /// <summary>The moment a pending request was recorded, or fail — the debounce has no other start.</summary>
    private static DateTimeOffset Pending(ExitStateBackupScheduler s)
    {
        var since = s.PendingSince(Store);
        Assert.NotNull(since);
        return since.Value;
    }

    // ------------------------------------------------------------------
    // The debounce: a burst becomes one window, and a later event cannot move it
    // ------------------------------------------------------------------

    [Fact]
    public void A_request_is_not_acted_on_until_the_debounce_interval_has_passed()
    {
        var s = new ExitStateBackupScheduler();

        // An existing backup anchors the safety net — a store that has never had one is due at once,
        // request or no request, and would answer "yes" here whatever the debounce said.
        s.RequestRefresh(Store);
        s.MarkTaken(Store, Pending(s));

        s.RequestRefresh(Store);
        var started = Pending(s);

        Assert.False(s.ShouldTake(Store, started + Min(1)));
        Assert.True(s.ShouldTake(Store, started + Min(2)));
    }

    [Fact]
    public void A_second_request_during_a_pending_window_does_not_move_its_deadline()
    {
        var s = new ExitStateBackupScheduler();
        s.RequestRefresh(Store);
        var started = Pending(s);

        // The coalescing rule, read where it is written: a throttle would push this timestamp
        // forward, and a wallet with a steady stream of events would then never schedule a backup.
        s.RequestRefresh(Store);
        Assert.Equal(started, Pending(s));
    }

    [Fact]
    public void Serving_a_pending_request_clears_it_and_a_new_request_starts_its_own_window()
    {
        var s = new ExitStateBackupScheduler();
        s.RequestRefresh(Store);
        var dueAt = Pending(s) + Min(2);
        Assert.True(s.ShouldTake(Store, dueAt));

        s.MarkTaken(Store, dueAt);
        Assert.Null(s.PendingSince(Store));

        s.RequestRefresh(Store);
        var second = Pending(s);

        // The new window starts at the new request's own stamp and runs two minutes of its own —
        // not at the last take, which is already a past fact by then.
        Assert.False(s.ShouldTake(Store, second + Min(1)));
        Assert.True(s.ShouldTake(Store, second + Min(2)));
    }

    // ------------------------------------------------------------------
    // The safety net: silence from the event stream is not a reason to stop backing up
    // ------------------------------------------------------------------

    [Fact]
    public void A_store_with_nothing_taken_yet_is_due_immediately()
    {
        var s = new ExitStateBackupScheduler();

        // No request, no pass, no history: a wallet that has never had a backup is the first thing
        // the first pass takes, not one safety-net interval from now.
        Assert.True(s.ShouldTake(Store, Base));
    }

    [Fact]
    public void The_safety_net_fires_after_its_interval_with_no_event_having_ever_arrived()
    {
        var s = new ExitStateBackupScheduler();
        s.MarkTaken(Store, Base);

        // Nothing was ever requested — the events went missing in both directions, which this
        // codebase has observed the SDK actually do — and the store still gets its next copy.
        Assert.False(s.ShouldTake(Store, Base + Min(30)));
        Assert.False(s.ShouldTake(Store, Base + Min(59)));
        Assert.True(s.ShouldTake(Store, Base + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_pass_that_found_the_state_unchanged_counts_as_a_pass_for_the_safety_net()
    {
        var s = new ExitStateBackupScheduler();

        s.RequestRefresh(Store);
        var passAt = Pending(s) + Min(2);
        s.MarkSkipped(Store, passAt);

        // "A pass happened and nothing changed" leaves the state known-current at this moment:
        // the next check is owed an interval from here, not from the last actual write.
        Assert.False(s.ShouldTake(Store, passAt + Min(30)));
        Assert.False(s.ShouldTake(Store, passAt + Min(59)));
        Assert.True(s.ShouldTake(Store, passAt + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_skipped_pass_serves_the_pending_request()
    {
        var s = new ExitStateBackupScheduler();
        s.RequestRefresh(Store);
        s.MarkSkipped(Store, Base);

        Assert.Null(s.PendingSince(Store));
        Assert.False(s.ShouldTake(Store, Base + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void An_idle_pass_with_nothing_pending_waits_the_safety_net_before_being_asked_again()
    {
        var s = new ExitStateBackupScheduler();

        // The unfunded wallet: asked, and answering nothing. That answer was still a pass, so the
        // next ask is an interval from here rather than the task's next minute — the difference
        // between one live export an hour and one every minute forever, for a wallet that has no
        // exit state to give.
        s.MarkIdlePass(Store, Base);

        Assert.False(s.ShouldTake(Store, Base + Min(30)));
        Assert.False(s.ShouldTake(Store, Base + Min(59)));
        Assert.True(s.ShouldTake(Store, Base + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void An_idle_pass_serves_nothing_and_a_later_take_serves_the_request()
    {
        var s = new ExitStateBackupScheduler();
        s.RequestRefresh(Store);
        var started = Pending(s);

        // Empty export with a request pending: the pass learned nothing about the state, so the
        // request a real event earned is still owed. Only a pass that can report on the wallet
        // serves it.
        s.MarkIdlePass(Store, Base);
        Assert.Equal(started, s.PendingSince(Store));

        s.MarkTaken(Store, Base + Min(2));
        Assert.Null(s.PendingSince(Store));
        Assert.False(s.ShouldTake(Store, Base + Min(3)));
    }

    // ------------------------------------------------------------------
    // The content hash: what makes "due" not mean "written"
    // ------------------------------------------------------------------

    [Fact]
    public void An_unknown_stored_state_is_never_reported_unchanged()
    {
        var s = new ExitStateBackupScheduler();

        // The state right after a restart: the file may hold anything, and a scheduler that said
        // "unchanged" from ignorance would leave a wallet's fresh state unsaved indefinitely.
        Assert.False(s.KnowsStoredContent(Store));
        Assert.False(s.ContentUnchanged(Store, "exported-blob"));
    }

    [Fact]
    public void Content_matching_what_was_recorded_reads_as_unchanged_and_different_content_does_not()
    {
        var s = new ExitStateBackupScheduler();
        s.NoteStoredContent(Store, "exported-blob");

        Assert.True(s.KnowsStoredContent(Store));
        Assert.True(s.ContentUnchanged(Store, "exported-blob"));
        Assert.False(s.ContentUnchanged(Store, "exported-bloq"));
    }

    [Fact]
    public void Recording_no_stored_content_is_a_known_state_and_any_export_is_then_a_change()
    {
        var s = new ExitStateBackupScheduler();
        s.NoteStoredContent(Store, "exported-blob");
        Assert.True(s.ContentUnchanged(Store, "exported-blob"));

        // "The file is absent" is not the same as "nothing has been seeded yet": it is the answer
        // the caller gave about the file, and from it any export — including the same one — is a
        // change worth writing back.
        s.NoteStoredContent(Store, null);
        Assert.False(s.KnowsStoredContent(Store));
        Assert.False(s.ContentUnchanged(Store, "exported-blob"));
    }

    [Fact]
    public void An_idle_pass_leaves_the_belief_about_stored_content_exactly_as_it_found_it()
    {
        var s = new ExitStateBackupScheduler();

        // Nothing noted: an export that came back empty is not evidence that the file is absent, and
        // a belief of "absent" would have the next due pass rewrite whatever the file does hold.
        s.MarkIdlePass(Store, Base);
        Assert.False(s.KnowsStoredContent(Store));

        // And a belief the caller already seeded from the file survives the pass: the empty answer
        // says nothing about the file, so it cannot be what the scheduler's belief is rewritten from.
        s.NoteStoredContent(Store, "exported-blob");
        s.MarkIdlePass(Store, Base + Min(30));
        Assert.True(s.ContentUnchanged(Store, "exported-blob"));
    }
}
