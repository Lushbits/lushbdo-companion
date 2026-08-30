namespace LushbdoCompanion;

/// <summary>
/// The silver balance's small sibling to <see cref="LineBoard"/> — and a
/// sibling rather than a mode on it, because almost nothing transfers. The
/// board's whole job is identity on a scroll stream: position-anchored, voted
/// across passes, every ambiguity resolved toward a visible undercount. A
/// balance is a **level, not a stream of events** — there is no scroll,
/// nothing to dedup, and re-reading the same figure ten times is agreement
/// rather than ten pickups.
///
/// ## One rectangle, and it is the market panel
///
/// Owner ruling (#22, 2026-08-30). The warehouse figure and the marketplace
/// figure were always one number shown in two places, and the app briefly read
/// both so they could check each other. They do not any more: **one rectangle,
/// aimed at the Central Market panel.**
///
/// What decided it is a real failure, not tidiness. The warehouse panel draws
/// its balance beside the Withdraw button, and that button's hover overlay
/// physically covers the last group of digits — the field read
/// `23,975,827` for `23,975,827,939` three times running (2026-08-30 16:02).
/// The market panel has nothing that hovers over its figure, so the ruling
/// removes the failure at its source rather than detecting it afterwards.
///
/// Be clear about what that gives up, because it is the sharp edge of this
/// whole feature. Occlusion is invisible to everything else here: the crop
/// really does contain a complete, well-formed, wrong number, so the strict
/// shape passes it (a truncated grouped number is still grouped), agreement
/// passes it (it repeats for as long as the overlay is up), and a crop-edge
/// check would miss it (the digits stop mid-crop where the overlay starts).
/// The second rectangle was the only thing that could see it. With one
/// rectangle there is no guard — the bet is that the market panel is never
/// overlaid, and that bet is the reason the ruling names a panel instead of
/// letting the rectangle go anywhere.
///
/// ## The gate is the cost story
///
/// A PaddleOCR pass is ~340 ms wall and ~0.74 core-seconds on the chat region;
/// an unnecessary one next to a running game is a visible fraction of a core,
/// not noise. The loot path's keyed-change gate cannot carry this: the keyer's
/// contract is *the chat's* — a bright core with true dark within reach, which
/// is what the game guarantees for text drawn over the world — and a market
/// panel's digits sit on an opaque mid-tone panel that may never go below the
/// outline threshold. Keyed, such a frame would be black every tick,
/// "unchanged" forever, and the region would silently never be read at all.
///
/// So the gate here is the inverse, and it assumes nothing about the game's
/// text: **stillness on raw pixels**. Those panels are opaque and static; the
/// world behind them is neither. Two consecutive frames near-identical means a
/// panel is up and there is something to read; anything else is scenery, and
/// is dropped before any keying or reading happens. Steady state is therefore
/// "no panel, no work", and one picture is read at most
/// <see cref="ReadsPerPicture"/> times however long it sits there — so a
/// rectangle left over a static piece of UI costs a handful of small-crop
/// passes once, not a pass a second forever.
///
/// ## Ambiguity resolves to stale, never to wrong
///
/// The loot rule is "a visible undercount, never a double count". The
/// equivalent here is "keep the figure the member already has rather than
/// record a misread one", because a balance has no register behind it: a
/// plausible wrong figure would land silently and stay. So a reading has to
/// repeat <see cref="AgreeingReads"/> times before it counts — a higher bar
/// than the board's two, since the failure this guards against (a thousand
/// read as a hundred) is a factor of ten rather than one pickup.
///
/// What that agreement is worth was the open question, and the first field
/// trace answered it better than expected: these panels are *not* frozen. They
/// drift between reads, so the agreeing passes are genuinely independent
/// captures rather than the same arithmetic over the same buffer. The vote is
/// therefore kept on the reading and not on the picture — the opposite of what
/// this class shipped with, which is why the real figure was read correctly
/// five times and confirmed none of them.
///
/// What agreement cannot catch is a misread the recognizer makes the same way
/// every time. The grouping-strict shape in <see cref="BalanceParser"/> is the
/// guard against that one, and the same trace showed how much work it does:
/// with a bare digit run allowed, a rectangle overlapping neighbouring UI
/// confirmed `0 Black` as 0 silver.
/// </summary>
public sealed class BalanceBoard
{
    /// <summary>
    /// Readings that must agree before a figure is called confirmed. Three,
    /// not the board's two: a dropped digit is a factor-of-ten error and there
    /// is no register downstream to catch it.
    /// </summary>
    public const int AgreeingReads = 3;

    /// <summary>
    /// Sampled mean-abs-diff below this and consecutive raw frames count as
    /// the same picture — a panel standing still. The same number
    /// <see cref="FrameStabilizer.MeanAbsDiff"/> computes for the loot gate,
    /// read the opposite way round.
    /// </summary>
    public const double StillGate = 1.0;

    /// <summary>
    /// How many passes one still picture is ever worth. Without this a panel
    /// that reads badly — or a rectangle left over static scenery — would be
    /// re-read every tick for as long as it sat there.
    /// </summary>
    public const int ReadsPerPicture = 6;

    /// <summary>
    /// How long the log may repeat itself — about two minutes at the watcher's
    /// pace, the same cadence as the watcher's own heartbeat.
    ///
    /// Both directions of this were wrong before. A repeat confirmation was
    /// suppressed entirely as "same value, nothing to say", so a rectangle that
    /// was reading and re-confirming perfectly went silent and read as broken —
    /// twice, to the person who built it (2026-08-30 16:15). And a refusal was
    /// deduplicated per *picture*, which these drifting panels manufacture
    /// constantly, so the same "there were no digits in it" line arrived every
    /// few seconds. Proof of life beats both.
    /// </summary>
    public const int RepeatNoteTicks = 240;

    private readonly Action<string> _note;
    private readonly Action<string>? _trace;
    private readonly Action<long>? _onConfirmed;

    private byte[] _previous = [];       // last tick's pixels — the stillness comparison
    private byte[] _lastRead = [];       // the picture the last committed pass read
    private bool _tracedStill;
    private bool _done;                  // confirmed or exhausted: this picture is finished with
    private int _readsThisPicture;
    private int _validReads;
    private long? _pendingValue;
    private int _pendingCount;
    private string _lastNote = "";
    private int _ticksSinceNote = RepeatNoteTicks;

    public BalanceBoard(Action<string> note, Action<string>? trace = null, Action<long>? onConfirmed = null)
    {
        _note = note;
        _trace = trace;
        _onConfirmed = onConfirmed;
    }

    /// <summary>The newest figure this session stands behind, or null while nothing has confirmed.</summary>
    public long? Confirmed { get; private set; }

    /// <summary>How many OCR passes the balance rectangle has cost, for the heartbeat.</summary>
    public long Reads { get; private set; }

    /// <summary>How many times a figure has been confirmed, for the heartbeat.</summary>
    public long Confirmations { get; private set; }

    /// <summary>
    /// The gate, run on every tick and before anything expensive: are these
    /// pixels a panel standing still, and is this picture still worth a read?
    /// Cheap by construction — one sampled diff over a digit-sized crop.
    /// </summary>
    public bool Observe(byte[] pixels, int length)
    {
        if (_ticksSinceNote < int.MaxValue) _ticksSinceNote++;

        if (_previous.Length != length)
        {
            // First frame, or the region resized under us. There is nothing to
            // compare against yet, so this tick is only a baseline.
            _previous = new byte[length];
            _lastRead = [];
            pixels.AsSpan(0, length).CopyTo(_previous);
            Forget();
            return false;
        }

        var still = FrameStabilizer.MeanAbsDiff(pixels, _previous, length) < StillGate;
        var changedSinceRead = _lastRead.Length != length ||
                               FrameStabilizer.MeanAbsDiff(pixels, _lastRead, length) >= StillGate;
        pixels.AsSpan(0, length).CopyTo(_previous);

        if (still != _tracedStill)
        {
            _tracedStill = still;
            _trace?.Invoke(still
                ? "bal   went still — a panel is up over the rectangle"
                : "bal   moving — no panel up, nothing to read");
        }
        if (!still) return false;

        // A picture that moved on gets a fresh read budget — but *not* a fresh
        // vote. Those were the same thing until the first field trace
        // (2026-08-30 15:45), where the real figure was read correctly five
        // times and confirmed none of them: these panels drift between reads,
        // every drift counted as a new question, and the count restarted at one
        // forever. Agreement belongs to the reading, not to the pixels — and
        // readings taken across a drift agree *more* meaningfully than ones
        // taken off a frozen buffer.
        if (changedSinceRead) NewPicture();
        return !_done && _readsThisPicture < ReadsPerPicture;
    }

    /// <summary>
    /// Called when the watcher actually commits the pass <see cref="Observe"/>
    /// approved — separate from it because the reader may be busy with the loot
    /// region, and a read that never happened must not count as this picture
    /// having been looked at.
    /// </summary>
    public void TakeRead()
    {
        if (_lastRead.Length != _previous.Length) _lastRead = new byte[_previous.Length];
        _previous.AsSpan().CopyTo(_lastRead);
        _readsThisPicture++;
        Reads++;
    }

    /// <summary>What the recognizer made of the crop, as one line of text.</summary>
    public void Ingest(string text)
    {
        var reading = BalanceParser.Parse(text);
        _trace?.Invoke($"bal   read \"{text}\" -> " +
                       (reading.Ok ? BalanceParser.Money(reading.Value) : "refused (" + reading.Why + ")"));

        if (reading.Ok)
        {
            _validReads++;
            if (_pendingValue == reading.Value) _pendingCount++;
            else
            {
                _pendingValue = reading.Value;
                _pendingCount = 1;
            }
        }
        else
        {
            _pendingValue = null;
            _pendingCount = 0;
            NoteOnce($"balance  the silver rectangle read \"{Clip(text)}\" and it was not used — " +
                     $"{BalanceParser.Describe(reading.Why)}.");
        }

        if (_pendingCount >= AgreeingReads && _pendingValue is { } value)
        {
            Settle(value);
            return;
        }

        if (_readsThisPicture >= ReadsPerPicture && !_done)
        {
            _done = true;
            if (_validReads > 0)
                _note($"balance  the figure was read {ReadsPerPicture} times without {AgreeingReads} agreeing " +
                      "readings — nothing confirmed, and whatever you already have stands.");
        }
    }

    private void Settle(long value)
    {
        _done = true;
        Confirmations++;
        var sameFigure = Confirmed == value;
        Confirmed = value;

        // Every settle, repeats included. What the site does about a figure it
        // already has is the sender's business and the route's, not this
        // board's — and reporting the same fact the same way each time is what
        // lets the sender be the only thing that tracks what was delivered.
        _onConfirmed?.Invoke(value);

        if (sameFigure && _ticksSinceNote < RepeatNoteTicks)
        {
            _trace?.Invoke($"bal   confirmed {value} again — unchanged");
            return;
        }
        _ticksSinceNote = 0;

        // A figure that changed says so at once; one that has not says so
        // periodically, because silence was read as breakage twice in one
        // session and a member cannot tell a working rectangle from a dead one.
        _note(sameFigure
            ? $"balance  still reading {BalanceParser.Money(value)} silver — unchanged."
            : $"balance  confirmed {BalanceParser.Money(value)} silver ({AgreeingReads} agreeing readings). " +
              "Nothing is sent — this is the log only.");
    }

    /// <summary>
    /// The frames stopped or the region moved: every picture on the screen now
    /// is unrelated to the one being voted on. The confirmed figure survives —
    /// stale beats wrong, and it is the log's memory, not a pending claim.
    /// </summary>
    public void Reset(string reason)
    {
        _trace?.Invoke($"bal   reset — {reason}");
        _previous = [];
        _lastRead = [];
        _tracedStill = false;
        Forget();
    }

    /// <summary>The picture moved on: a fresh budget of passes, and nothing said about the old one.</summary>
    private void NewPicture()
    {
        _done = false;
        _readsThisPicture = 0;
        _validReads = 0;
    }

    /// <summary>A discontinuity. Nothing carries across one, the vote included.</summary>
    private void Forget()
    {
        NewPicture();
        _pendingValue = null;
        _pendingCount = 0;
    }

    /// <summary>The read, short enough for one log line and never reformatted.</summary>
    private static string Clip(string text)
    {
        var t = text.Trim();
        return t.Length <= 60 ? t : t[..60] + "…";
    }

    /// <summary>
    /// Say it once, and then not again until the window passes. Keyed on the
    /// message rather than the picture: these panels drift constantly, and
    /// per-picture deduplication meant the same refusal every few seconds.
    /// </summary>
    private void NoteOnce(string message)
    {
        if (_lastNote == message && _ticksSinceNote < RepeatNoteTicks) return;
        _lastNote = message;
        _ticksSinceNote = 0;
        _note(message);
    }
}
