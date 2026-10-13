using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Speech;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The mind's voice policy, verified headless: bounded queue with honest
/// oldest-drop, no hard swaps (a mid-cycle arrival waits), duplicate re-pushes are
/// spoken once, and the EMERGENCY STOP gates proactive lines to honest silence
/// (purging the queued ones too) while presence digests may still show.
/// </summary>
public class SpeechPresenterTests
{
    private static ProactiveMessage Proactive(string summary = "Стоит разобраться.") =>
        new(MessageKind.Proactive, summary, null);

    private static ProactiveMessage Presence(string summary = "За последний час: 3 задачи.") =>
        new(MessageKind.PresenceReturn, summary, null);

    private sealed class Recorder
    {
        public List<ProactiveMessage> Shown { get; } = [];

        public Recorder Attach(SpeechPresenter presenter)
        {
            presenter.SpeechStarted += message => Shown.Add(message);
            return this;
        }
    }

    [Fact]
    public void First_message_shows_immediately_and_nothing_is_queued()
    {
        var presenter = new SpeechPresenter();
        var recorder = new Recorder().Attach(presenter);

        presenter.OnMessage(Proactive());

        Assert.Equal([Proactive()], recorder.Shown); // record equality: kind + summary + at
        Assert.True(presenter.IsShowing);
        Assert.Equal(0, presenter.QueuedCount);
    }

    [Fact]
    public void Message_while_showing_queues_and_never_hard_swaps()
    {
        var presenter = new SpeechPresenter();
        var recorder = new Recorder().Attach(presenter);
        presenter.OnMessage(Proactive("первая"));

        presenter.OnMessage(Proactive("вторая"));

        Assert.Single(recorder.Shown);            // the visible line keeps its cycle
        Assert.Equal(1, presenter.QueuedCount);
    }

    [Fact]
    public void Cycle_completion_advances_the_queue_in_order()
    {
        var presenter = new SpeechPresenter();
        var recorder = new Recorder().Attach(presenter);
        presenter.OnMessage(Proactive("первая"));
        presenter.OnMessage(Proactive("вторая"));
        presenter.OnMessage(Proactive("третья"));

        presenter.NotifyCycleCompleted();

        Assert.Equal(2, recorder.Shown.Count);
        Assert.Equal("вторая", recorder.Shown[1].Summary);
        Assert.True(presenter.IsShowing);            // the queue advances immediately…
        Assert.Equal("вторая", presenter.Showing!.Summary);
        Assert.Equal(1, presenter.QueuedCount);

        presenter.NotifyCycleCompleted();
        presenter.NotifyCycleCompleted();            // …and only truly idles when empty

        Assert.Equal(3, recorder.Shown.Count);
        Assert.False(presenter.IsShowing);
        Assert.Equal(0, presenter.QueuedCount);
    }

    [Fact]
    public void Cycle_completion_with_empty_queue_returns_to_idle()
    {
        var presenter = new SpeechPresenter();
        new Recorder().Attach(presenter);
        presenter.OnMessage(Proactive());

        presenter.NotifyCycleCompleted();
        presenter.NotifyCycleCompleted(); // a stray second completion is harmless

        Assert.False(presenter.IsShowing);
        Assert.Equal(0, presenter.QueuedCount);
    }

    [Fact]
    public void Overflow_drops_the_oldest_queued_honestly()
    {
        var presenter = new SpeechPresenter();
        var recorder = new Recorder().Attach(presenter);
        presenter.OnMessage(Proactive("на ленте"));      // showing
        presenter.OnMessage(Proactive("ждёт-1"));
        presenter.OnMessage(Proactive("ждёт-2"));
        presenter.OnMessage(Proactive("ждёт-3"));        // queue is now full (3)

        presenter.OnMessage(Proactive("ждёт-4"));        // overflow → ждёт-1 is dropped

        Assert.Equal(3, presenter.QueuedCount);
        presenter.NotifyCycleCompleted();                // → ждёт-2
        presenter.NotifyCycleCompleted();                // → ждёт-3
        presenter.NotifyCycleCompleted();                // → ждёт-4
        Assert.Equal(
            ["на ленте", "ждёт-2", "ждёт-3", "ждёт-4"],
            recorder.Shown.Select(message => message.Summary));
    }

    [Fact]
    public void Identical_repush_while_showing_is_not_spoken_twice()
    {
        // presence_return arrives on EVERY reconnect — the same words re-pushed
        // while visible are the same fact, not new speech.
        var presenter = new SpeechPresenter();
        var recorder = new Recorder().Attach(presenter);
        presenter.OnMessage(Presence());

        presenter.OnMessage(Presence());

        Assert.Single(recorder.Shown);
        Assert.Equal(0, presenter.QueuedCount);
    }

    [Fact]
    public void Identical_repush_while_queued_is_dropped()
    {
        var presenter = new SpeechPresenter();
        var recorder = new Recorder().Attach(presenter);
        presenter.OnMessage(Proactive("на ленте"));
        presenter.OnMessage(Proactive("ждёт"));

        presenter.OnMessage(Proactive("ждёт"));

        Assert.Equal(1, presenter.QueuedCount);
    }

    [Fact]
    public void A_new_digest_is_new_speech_even_after_an_old_one()
    {
        var presenter = new SpeechPresenter();
        var recorder = new Recorder().Attach(presenter);
        presenter.OnMessage(Presence("За последний час: 1 задача."));

        presenter.OnMessage(Presence("За последний час: 3 задачи."));

        Assert.Equal(1, presenter.QueuedCount); // different summary → genuinely new
    }

    [Fact]
    public void Emergency_stop_silences_new_proactive_lines()
    {
        var presenter = new SpeechPresenter();
        var recorder = new Recorder().Attach(presenter);
        presenter.SetEmergencyStopped(true);

        presenter.OnMessage(Proactive());

        Assert.Empty(recorder.Shown);
        Assert.False(presenter.IsShowing);
    }

    [Fact]
    public void Emergency_stop_still_allows_presence_digests()
    {
        var presenter = new SpeechPresenter();
        var recorder = new Recorder().Attach(presenter);
        presenter.SetEmergencyStopped(true);

        presenter.OnMessage(Presence());

        Assert.Single(recorder.Shown);
        Assert.True(presenter.IsShowing);
    }

    [Fact]
    public void Engaging_the_stop_purges_queued_proactive_but_keeps_digests()
    {
        var presenter = new SpeechPresenter();
        var recorder = new Recorder().Attach(presenter);
        presenter.OnMessage(Proactive("на ленте"));   // showing — finishes its cycle
        presenter.OnMessage(Proactive("совет"));
        presenter.OnMessage(Presence("дайджест"));

        presenter.SetEmergencyStopped(true);
        Assert.Equal(1, presenter.QueuedCount);       // only the digest survived

        presenter.NotifyCycleCompleted();             // the spoken line ends naturally
        Assert.Equal(
            ["на ленте", "дайджест"],                 // the advice never surfaced
            recorder.Shown.Select(message => message.Summary));
        Assert.Equal("дайджест", presenter.Showing!.Summary);
    }

    [Fact]
    public void Resuming_reopens_the_voice()
    {
        var presenter = new SpeechPresenter();
        var recorder = new Recorder().Attach(presenter);
        presenter.SetEmergencyStopped(true);
        presenter.OnMessage(Proactive());
        Assert.Empty(recorder.Shown);

        presenter.SetEmergencyStopped(false);
        presenter.OnMessage(Proactive());

        Assert.Single(recorder.Shown);
    }

    [Fact]
    public void Stop_state_is_idempotent_and_does_not_touch_the_queue_twice()
    {
        var presenter = new SpeechPresenter();
        var recorder = new Recorder().Attach(presenter);
        presenter.OnMessage(Proactive("на ленте"));   // showing
        presenter.OnMessage(Presence("дайджест-1"));  // queued
        presenter.OnMessage(Presence("дайджест-2"));  // queued

        presenter.SetEmergencyStopped(true);
        presenter.SetEmergencyStopped(true); // no double purge, no invented state

        Assert.Equal(2, presenter.QueuedCount);
        Assert.True(presenter.EmergencyStopped);
        Assert.Single(recorder.Shown);       // no purge ever started a line early
    }

    [Fact]
    public void Null_message_is_rejected_not_crashed()
    {
        var presenter = new SpeechPresenter();

        Assert.Throws<ArgumentNullException>(() => presenter.OnMessage(null!));
    }
}
