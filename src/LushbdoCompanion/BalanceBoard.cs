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
/// Owner ruling (#22): the warehouse figure and the marketplace figure are one
/// number shown in two places, never two numbers to add. Either reading is the
/// whole answer, and if they disagree that is proof one of them was misread.
///
/// ## The gate is the cost story
///
/// A PaddleOCR pass is ~340 ms wall and ~0.74 core-seconds on the chat region;
/// an unnecessary one next to a running game is a visible fraction of a core,
/// not noise. The loot path's keyed-change gate cannot carry this: the keyer's
/// contract is *the chat's* — a bright core with true dark within reach, which
/// is what the game guarantees for text drawn over the world — and a warehouse
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
/// read as a hundred) is a factor of ten rather than one pickup — and two
/// panels that disagree confirm nothing at all.
///
/// What that agreement is worth was the open question, and the first field
/// trace answered it better than expected: these panels are *not* frozen. They
/// drift between reads, so the agreeing passes are genuinely independent
/// captures rather than the same arithmetic over the same buffer. The vote is
/// therefore kept on the reading and not on the picture — the opposite of what
/// this class shipped with, which is why the real figure was read correctly
/// five times and confirmed none of them.
///
/// What agreement still cannot catch is a misread the recognizer makes the
/// same way every time. The grouping-strict shape in
/// <see cref="BalanceParser"/> is the whole guard against that one, and the
/// same trace showed how much work it does: with a bare digit run allowed, a
/// rectangle overlapping neighbouring UI confirmed `0 Black` as 0 silver.
/// </summary>
public sealed class BalanceBoard
{
    /// <summary>The two places the one balance shows.</summary>
    public enum Panel { Warehouse, Marketplace }

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
    /// How long one rectangle's last clean reading stays usable as evidence
    /// against the other's — about ten seconds at the watcher's pace.
    ///
    /// This exists because the cross-panel rule was written and then could not
    /// fire. It asked whether the other rectangle was reading a different
    /// figure *at that instant*, and the answer was almost always "it is not
    /// reading anything": a single empty pass or one drift wiped what it had
    /// last seen. The field showed the cost (2026-08-30 16:02) — one rectangle
    /// read 23,975,827,939 while the other confirmed 23,975,827, three
    /// degraded passes agreeing with each other, and nothing compared them.
    ///
    /// A reading a few seconds old is still evidence: the balance cannot have
    /// changed in the meantime without the member doing something that takes
    /// far longer. Stale evidence ages out rather than being thrown away, and
    /// while it stands, a contradiction blocks both figures.
    /// </summary>
    public const int CrossCheckTicks = 20;

    private readonly Action<string> _note;
    private readonly Action<string>? _trace;
    private readonly PanelState[] _panels = [new(Panel.Warehouse), new(Panel.Marketplace)];

    /// <summary>Which rectangle produced the standing figure, so a second one agreeing is visible.</summary>
    private Panel? _confirmedBy;

    public BalanceBoard(Action<string> note, Action<string>? trace = null)
    {
        _note = note;
        _trace = trace;
    }

    /// <summary>The newest figure this session stands behind, or null while nothing has confirmed.</summary>
    public long? Confirmed { get; private set; }

    /// <summary>How many OCR passes the balance regions have cost, for the heartbeat.</summary>
    public long Reads { get; private set; }

    /// <summary>How many times a figure has been confirmed, for the heartbeat.</summary>
    public long Confirmations { get; private set; }

    /// <summary>
    /// The gate, run on every tick and before anything expensive: are these
    /// pixels a panel standing still, and is this picture still worth a read?
    /// Cheap by construction — one sampled diff over a digit-sized crop.
    /// </summary>
    public bool Observe(Panel panel, byte[] pixels, int length)
    {
        var p = State(panel);

        // What this rectangle last read is evidence for a while, not only on
        // the tick it read it. Ageing it here — every tick, whatever else the
        // gate decides — is what lets the cross-panel rule actually fire.
        if (p.LastValue is not null && ++p.LastValueAge > CrossCheckTicks)
        {
            p.LastValue = null;
            p.LastValueAge = 0;
        }

        if (p.Previous.Length != length)
        {
            // First frame, or the region resized under us. There is nothing to
            // compare against yet, so this tick is only a baseline.
            p.Previous = new byte[length];
            p.LastRead = [];
            pixels.AsSpan(0, length).CopyTo(p.Previous);
            p.StillNow = false;
            p.Forget();
            return false;
        }

        var still = FrameStabilizer.MeanAbsDiff(pixels, p.Previous, length) < StillGate;
        var changedSinceRead = p.LastRead.Length != length ||
                               FrameStabilizer.MeanAbsDiff(pixels, p.LastRead, length) >= StillGate;
        pixels.AsSpan(0, length).CopyTo(p.Previous);

        p.StillNow = still;
        if (still != p.TracedStill)
        {
            p.TracedStill = still;
            _trace?.Invoke(still
                ? $"bal   {panel} went still — a panel is up over the rectangle"
                : $"bal   {panel} moving — no panel up, nothing to read");
        }
        if (!still) return false;

        // A picture that moved on gets a fresh read budget — but *not* a fresh
        // vote. Those were the same thing until the first field trace
        // (2026-08-30 15:45), where the real figure was read correctly five
        // times and confirmed none of them: these panels drift between reads,
        // every drift counted as a new question, and the count restarted at
        // one forever. Agreement belongs to the reading, not to the pixels —
        // and readings taken across a drift agree *more* meaningfully than
        // ones taken off a frozen buffer, which is the whole worry recorded in
        // this class's summary.
        if (changedSinceRead) p.NewPicture();
        return !p.Done && p.ReadsThisPicture < ReadsPerPicture;
    }

    /// <summary>
    /// Called when the watcher actually commits the pass <see cref="Observe"/>
    /// approved — separate from it because the reader may be busy with the
    /// loot region, and a read that never happened must not count as this
    /// picture having been looked at.
    /// </summary>
    public void TakeRead(Panel panel)
    {
        var p = State(panel);
        if (p.LastRead.Length != p.Previous.Length) p.LastRead = new byte[p.Previous.Length];
        p.Previous.AsSpan().CopyTo(p.LastRead);
        p.ReadsThisPicture++;
        Reads++;
    }

    /// <summary>What the recognizer made of the crop, as one line of text.</summary>
    public void Ingest(Panel panel, string text)
    {
        var p = State(panel);
        var reading = BalanceParser.Parse(text);
        _trace?.Invoke($"bal   {panel} read \"{text}\" -> " +
                       (reading.Ok ? BalanceParser.Money(reading.Value) : "refused (" + reading.Why + ")"));

        if (reading.Ok)
        {
            p.LastValue = reading.Value;
            p.LastValueAge = 0;
            p.ValidReads++;
            if (p.PendingValue == reading.Value) p.PendingCount++;
            else
            {
                p.PendingValue = reading.Value;
                p.PendingCount = 1;
            }
        }
        else
        {
            // LastValue is deliberately left alone: what this rectangle last
            // read stays evidence against the other one until it ages out. A
            // panel that has just been closed reads as nothing, and "nothing"
            // is not a retraction of what it said two seconds ago.
            p.PendingValue = null;
            p.PendingCount = 0;
            // Once per picture: a panel that reads badly reads badly on every
            // pass, and six identical lines say nothing the first did not.
            NoteOnce(p, $"balance  the {Name(panel)} rectangle read \"{Clip(text)}\" and it was not used — " +
                        $"{BalanceParser.Describe(reading.Why)}.");
        }

        if (p.PendingCount >= AgreeingReads && p.PendingValue is { } value)
        {
            Settle(p, value);
            return;
        }

        if (p.ReadsThisPicture >= ReadsPerPicture && !p.Done)
        {
            p.Done = true;
            if (p.ValidReads > 0)
                _note($"balance  the {Name(panel)} figure was read {ReadsPerPicture} times without {AgreeingReads} " +
                      "agreeing readings — nothing confirmed, and whatever you already have stands.");
        }
    }

    private void Settle(PanelState p, long value)
    {
        p.Done = true;

        // The two panels show one number. Disagreement is proof of a misread,
        // and the only safe answer is the figure the member already has. The
        // other rectangle does not have to be readable *right now* for its last
        // reading to contradict this one — see CrossCheckTicks for why that
        // was the difference between a rule and a rule that fires.
        var other = State(p.Panel == Panel.Warehouse ? Panel.Marketplace : Panel.Warehouse);
        if (other.LastValue is { } theirs && theirs != value)
        {
            _note($"balance  the {Name(p.Panel)} rectangle reads {BalanceParser.Money(value)} while the " +
                  $"{Name(other.Panel)} one reads {BalanceParser.Money(theirs)}. They are two views of the same " +
                  "silver, so one of them is misread — nothing confirmed.");
            return;
        }

        Confirmations++;
        var sameFigure = Confirmed == value;
        var otherPanel = _confirmedBy is { } who && who != p.Panel;
        Confirmed = value;
        _confirmedBy = p.Panel;

        if (sameFigure && !otherPanel)
        {
            _trace?.Invoke($"bal   {p.Panel} confirmed {value} again — unchanged");
            return;
        }
        // The second rectangle agreeing is the cross-check *passing*, and a
        // member who has just aimed one deserves to see it work. Silence is
        // what a re-read of the same picture earns, not a second view of it.
        _note(sameFigure
            ? $"balance  the {Name(p.Panel)} rectangle confirms the same {BalanceParser.Money(value)} silver — " +
              "both views agree."
            : $"balance  confirmed {BalanceParser.Money(value)} silver from the {Name(p.Panel)} " +
              $"({AgreeingReads} agreeing readings). Nothing is sent — this is the log only.");
    }

    /// <summary>
    /// The frames stopped or the region moved: every picture on the screen now
    /// is unrelated to the one being voted on. The confirmed figure survives —
    /// stale beats wrong, and it is the log's memory, not a pending claim.
    /// </summary>
    public void Reset(string reason)
    {
        _trace?.Invoke($"bal   reset — {reason}");
        foreach (var p in _panels)
        {
            p.Previous = [];
            p.LastRead = [];
            p.StillNow = false;
            p.TracedStill = false;
            p.Forget();
        }
    }

    private PanelState State(Panel panel) => _panels[(int)panel];

    private static string Name(Panel panel) => panel == Panel.Warehouse ? "warehouse" : "marketplace";

    /// <summary>The read, short enough for one log line and never reformatted.</summary>
    private static string Clip(string text)
    {
        var t = text.Trim();
        return t.Length <= 60 ? t : t[..60] + "…";
    }

    private void NoteOnce(PanelState p, string message)
    {
        if (p.LastNote == message) return;
        p.LastNote = message;
        _note(message);
    }

    private sealed class PanelState(Panel panel)
    {
        public readonly Panel Panel = panel;

        /// <summary>Last tick's pixels — the stillness comparison.</summary>
        public byte[] Previous = [];

        /// <summary>The picture the last committed pass read — the "is this still the same question" comparison.</summary>
        public byte[] LastRead = [];

        public bool StillNow;
        public bool TracedStill;

        /// <summary>Confirmed, contradicted or exhausted: this picture is finished with.</summary>
        public bool Done;

        public int ReadsThisPicture;
        public int ValidReads;
        public long? PendingValue;
        public int PendingCount;

        /// <summary>
        /// The last figure this rectangle parsed cleanly — evidence against
        /// the other one until it ages past <see cref="CrossCheckTicks"/>. It
        /// deliberately survives a refusal and a new picture; only a
        /// discontinuity or the ageing clears it.
        /// </summary>
        public long? LastValue;

        public int LastValueAge;

        public string LastNote = "";

        /// <summary>
        /// The picture moved on: a fresh budget of passes, and nothing said
        /// about the old one. The vote deliberately survives — see the note at
        /// the call site. <see cref="LastValue"/> does not, because the
        /// cross-panel check asks what the other panel is showing *now*.
        /// </summary>
        public void NewPicture()
        {
            Done = false;
            ReadsThisPicture = 0;
            ValidReads = 0;
            LastNote = "";
        }

        /// <summary>A discontinuity — the frames stopped, the region moved. Nothing carries across one.</summary>
        public void Forget()
        {
            NewPicture();
            PendingValue = null;
            PendingCount = 0;
            LastValue = null;
            LastValueAge = 0;
        }
    }
}
