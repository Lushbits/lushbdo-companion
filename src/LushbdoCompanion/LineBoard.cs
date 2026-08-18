namespace LushbdoCompanion;

/// <summary>
/// The dedup heart of milestone (c). Every OCR pass over the stabilized image
/// yields the lines currently visible; this board decides which of them are
/// *new* — the one question content alone cannot answer, because the loot log
/// repeats identical lines with minute-granularity timestamps (bdo#581).
///
/// Identity comes from position on the scroll stream: trackers follow each
/// physical line across passes. The scroll offset between passes is measured
/// by voting — every exact match between a pass line's text and a tracker's
/// recorded readings votes for the shift that would explain it — which keys
/// alignment on stable text, not pixels (#2: raw pixels fail over the
/// transparent background, and the stabilized image ghosts for the first few
/// ticks after a scroll, exactly when alignment matters most). Each distinct
/// text carries one text's worth of vote no matter how many places it shows:
/// burst loot is near-identical (same items, same counts, same minute) and
/// its duplicate matches are periodic — unnormalized, they out-vote the few
/// unique lines that pin the true shift.
///
/// Emission is gated by reading consensus: nothing is ever sent on one
/// frame's word. A tracker emits once, when some parseable reading has
/// recurred; misreads vary randomly between frames while the true reading
/// repeats. Every ambiguity resolves the same direction — a visible
/// undercount, never a double count and never a guess: lines visible at
/// start are baseline and never sent, a lost alignment resets to baseline,
/// backwards scrolling that persists resets to baseline (one backwards vote
/// only holds fire — a burst can fake one), a wrapped head whose tail never
/// arrives is skipped aloud.
///
/// Single-threaded by contract: the watcher's one-OCR-in-flight gate is the
/// lock.
/// </summary>
public sealed class LineBoard(Action<string, int, string> emit, Action<string> note)
{
    /// <summary>A parseable reading must recur this many times before it is believed.</summary>
    public const int ConsensusReads = 2;

    private const int StaleAfterPasses = 6;     // vanished this long → the line is gone (faded, cleared)
    private const int NullPassesBeforeReset = 3; // no text matched anything this long → we are lost
    private const int BackwardsPassesBeforeReset = 2; // consecutive backwards votes before they are believed
    private const int MaxTrackers = 64;
    private const int MaxReadingsPerTracker = 12;
    private const double DyBinPx = 3.0;

    private sealed class Tracker
    {
        public double Y;
        public readonly Dictionary<string, int> Readings = new(StringComparer.Ordinal);
        public int PassesUnseen;
        public bool Emitted;            // sent, consumed as a tail, skipped, or adopted as baseline
        public string? SettledText;
        public LootParser.Reading Settled;
        public bool MatchedThisPass;
    }

    public readonly record struct OcrLineInput(string Text, double Y, double Height);

    private readonly List<Tracker> _trackers = [];
    private readonly List<OcrLineInput> _lastLines = [];
    private bool _baselinePending = true;
    private int _nullPasses;
    private int _backwardsPasses;
    private double _lineHeight = 18;

    /// <summary>True while any tracked line still awaits consensus or a wrapped tail.</summary>
    public bool HasUnsettled => _trackers.Exists(t => !t.Emitted);

    /// <summary>
    /// The stabilized image did not change since the last OCR pass, so the
    /// previous readings hold for another tick — feed them back in. This is
    /// what lets a line settle while the scene is perfectly still, without
    /// paying for another OCR pass.
    /// </summary>
    public void Reconfirm()
    {
        if (_baselinePending || _lastLines.Count == 0 || !HasUnsettled) return;
        IngestCore(_lastLines, fresh: false);
    }

    public void Ingest(IReadOnlyList<OcrLineInput> lines)
    {
        _lastLines.Clear();
        _lastLines.AddRange(lines);
        _lastLines.Sort((a, b) => a.Y.CompareTo(b.Y));
        IngestCore(_lastLines, fresh: true);
    }

    /// <summary>The screen is no longer the screen we knew (resize, restart). Everything visible next is old.</summary>
    public void Reset(string reason) => ResetForRealign(reason);

    private void IngestCore(List<OcrLineInput> lines, bool fresh)
    {
        if (lines.Count > 0)
            _lineHeight = Math.Clamp(MedianHeight(lines), 8, 64);

        if (_baselinePending)
        {
            AdoptBaseline(lines);
            return;
        }

        // How far did the chat scroll since last pass? Every exact text match
        // between a visible line and a tracker's readings votes for the shift
        // that would explain it; the weighted mode wins. Identical repeated
        // lines vote for several shifts, but every *other* stable line votes
        // only for the true one.
        double dy;
        if (_trackers.Count == 0)
        {
            dy = 0;
        }
        else if (VoteScroll(lines) is { } voted)
        {
            _nullPasses = 0;
            dy = voted;
            if (dy > _lineHeight)
            {
                // Content moved DOWN: the member scrolled the tab backwards —
                // or a loot burst's duplicate votes faked it for one pass. A
                // real backwards scroll keeps voting backwards because the
                // board holds still; a burst moves on. So hold — match
                // nothing, emit nothing — and realign only when a second
                // fresh read agrees: everything "revealed" below is then old
                // lines we already counted, indistinguishable from new ones —
                // realign rather than repeat.
                if (fresh && ++_backwardsPasses >= BackwardsPassesBeforeReset)
                    ResetForRealign("the chat scrolled backwards");
                return;
            }
            if (fresh) _backwardsPasses = 0;
        }
        else if (_trackers.TrueForAll(t => !t.Emitted))
        {
            // No text matched anything, but nothing on the board has emitted
            // yet either — a positional guess cannot double-count what was
            // never counted. Assume no scroll, so a brand-new line whose
            // first reading was a mangle can still pool toward consensus.
            dy = 0;
        }
        else
        {
            if (++_nullPasses >= NullPassesBeforeReset)
                ResetForRealign("the chat stopped reading recognizably");
            return;
        }

        foreach (var t in _trackers)
        {
            t.Y += dy;
            t.MatchedThisPass = false;
        }

        MatchAndTrack(lines);
        DropDepartedTrackers();
        if (_trackers.Count > MaxTrackers)
        {
            ResetForRealign("too many lines to track");
            return;
        }

        SettleAndEmit();
    }

    private void AdoptBaseline(List<OcrLineInput> lines)
    {
        foreach (var line in lines)
        {
            var t = new Tracker { Y = line.Y, Emitted = true };
            t.Readings[line.Text] = 1;
            _trackers.Add(t);
        }
        _baselinePending = false;
        _nullPasses = 0;
        if (lines.Count > 0)
            note($"Baseline read — the {lines.Count} line(s) already on screen are old; new pickups from here on are counted.");
    }

    private double? VoteScroll(List<OcrLineInput> lines)
    {
        // A text visible k times against m trackers holding it makes k×m
        // pairs, at most one per visible copy true. Splitting each text's
        // vote across its pairs caps every text at one text's worth of say:
        // a burst of near-identical drops casts its duplicate votes at pitch
        // multiples — coherent enough, unnormalized, to out-vote the unique
        // lines that pin the true shift and read as a backwards scroll.
        Dictionary<string, int>? pairCounts = null;
        foreach (var line in lines)
        {
            foreach (var t in _trackers)
            {
                if (!t.Readings.ContainsKey(line.Text)) continue;
                pairCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
                pairCounts.TryGetValue(line.Text, out var n);
                pairCounts[line.Text] = n + 1;
            }
        }
        if (pairCounts is null) return null;

        Dictionary<int, (double Weight, double DySum)> bins = [];
        foreach (var line in lines)
        {
            foreach (var t in _trackers)
            {
                if (!t.Readings.TryGetValue(line.Text, out var reads)) continue;
                var weight = (double)reads / pairCounts[line.Text];
                var dy = line.Y - t.Y;
                var bin = (int)Math.Round(dy / DyBinPx);
                bins.TryGetValue(bin, out var acc);
                bins[bin] = (acc.Weight + weight, acc.DySum + dy * weight);
            }
        }

        var best = default(KeyValuePair<int, (double Weight, double DySum)>);
        foreach (var bin in bins)
        {
            // Ties break toward the smaller shift: with nothing to tell two
            // offsets apart, the one that claims less new content wins —
            // undercount, never double count.
            if (bin.Value.Weight > best.Value.Weight ||
                (bin.Value.Weight == best.Value.Weight && Math.Abs(bin.Key) < Math.Abs(best.Key)))
                best = bin;
        }
        return best.Value.Weight == 0 ? null : best.Value.DySum / best.Value.Weight;
    }

    private void MatchAndTrack(List<OcrLineInput> lines)
    {
        var tolerance = 0.6 * _lineHeight;
        foreach (var line in lines)
        {
            Tracker? nearest = null;
            var nearestDist = double.MaxValue;
            foreach (var t in _trackers)
            {
                if (t.MatchedThisPass) continue;
                var dist = Math.Abs(t.Y - line.Y);
                if (dist < nearestDist)
                {
                    nearest = t;
                    nearestDist = dist;
                }
            }

            if (nearest is not null && nearestDist <= tolerance)
            {
                nearest.MatchedThisPass = true;
                nearest.PassesUnseen = 0;
                nearest.Y = line.Y; // re-anchor: measured position beats accumulated shifts
                AddReading(nearest, line.Text);
            }
            else
            {
                var t = new Tracker { Y = line.Y };
                t.Readings[line.Text] = 1;
                t.MatchedThisPass = true;
                _trackers.Add(t);
            }
        }
        _trackers.Sort((a, b) => a.Y.CompareTo(b.Y));
    }

    private static void AddReading(Tracker t, string text)
    {
        if (t.Readings.TryGetValue(text, out var n))
        {
            t.Readings[text] = n + 1;
            return;
        }
        if (t.Readings.Count >= MaxReadingsPerTracker)
        {
            // Full of one-off mangles: evict one to keep room for a reading
            // that might be the recurring truth.
            string? evict = null;
            foreach (var r in t.Readings)
                if (r.Value == 1) { evict = r.Key; break; }
            if (evict is null) return;
            t.Readings.Remove(evict);
        }
        t.Readings[text] = 1;
    }

    private void DropDepartedTrackers()
    {
        var emptiedByStaleness = false;
        for (var i = _trackers.Count - 1; i >= 0; i--)
        {
            var t = _trackers[i];
            if (!t.MatchedThisPass) t.PassesUnseen++;

            var scrolledOff = t.Y < -0.5 * _lineHeight;
            var stale = t.PassesUnseen >= StaleAfterPasses;
            if (!scrolledOff && !stale) continue;

            _trackers.RemoveAt(i);
            if (stale && !scrolledOff) emptiedByStaleness = true;
            if (t.Emitted) continue;

            note(t switch
            {
                { SettledText: not null, Settled.Kind: LootParser.Kind.NameOnly } =>
                    $"skip  \"{t.SettledText}\" — wrapped line whose quantity never arrived",
                { SettledText: not null, Settled.Kind: LootParser.Kind.NameOpen } =>
                    $"skip  \"{t.SettledText}\" — wrapped name whose ending never arrived",
                _ => $"skip  \"{ModalReading(t)}\" — never read cleanly before it scrolled away"
            });
        }

        if (_trackers.Count == 0 && emptiedByStaleness)
        {
            // Every line vanished without scrolling off — we went blind (a
            // storm of mangled frames, a cleared tab). What is visible when
            // reading resumes may be lines we already counted: baseline again.
            _baselinePending = true;
            _lastLines.Clear();
        }
    }

    private void SettleAndEmit()
    {
        foreach (var t in _trackers)
        {
            if (t.Emitted || t.SettledText is not null) continue;
            string? bestText = null;
            var bestCount = 0;
            LootParser.Reading bestParsed = default;
            foreach (var r in t.Readings)
            {
                if (r.Value < ConsensusReads || r.Value <= bestCount) continue;
                var parsed = LootParser.Parse(r.Key);
                if (parsed.Kind == LootParser.Kind.Unrecognized) continue;
                bestText = r.Key;
                bestCount = r.Value;
                bestParsed = parsed;
            }
            if (bestText is null) continue;
            t.SettledText = bestText;
            t.Settled = bestParsed;
        }

        for (var i = 0; i < _trackers.Count; i++)
        {
            var t = _trackers[i];
            if (t.Emitted || t.SettledText is null) continue;

            switch (t.Settled.Kind)
            {
                case LootParser.Kind.Item:
                    t.Emitted = true;
                    emit(t.Settled.Name, t.Settled.Count, t.SettledText);
                    break;

                case LootParser.Kind.Silver:
                    t.Emitted = true;
                    note($"skip  \"{t.SettledText}\" — silver is currency, not sent");
                    break;

                case LootParser.Kind.NameOnly:
                case LootParser.Kind.NameOpen:
                    EmitWrappedHead(t, i < _trackers.Count - 1 ? _trackers[i + 1] : null);
                    break;

                case LootParser.Kind.TimestampTail:
                    // The wrapped timestamp of the (already complete) line
                    // above; carries nothing we would use — capture time is
                    // the only clock.
                    t.Emitted = true;
                    break;

                case LootParser.Kind.QuantityTail:
                    // Consumed by a waiting head above, which runs first in
                    // this loop. Reaching it unclaimed means its head read as
                    // complete or never settled — never guess whose it was.
                    t.Emitted = true;
                    note($"skip  \"{t.SettledText}\" — a stray quantity with no line to belong to");
                    break;

                case LootParser.Kind.NameTail:
                    // Same contract as QuantityTail: only ever consumed by
                    // the open-bracket head directly above. Unclaimed means
                    // that head never settled — never guess whose name it
                    // finishes.
                    t.Emitted = true;
                    note($"skip  \"{t.SettledText}\" — the rest of a wrapped name whose start never settled");
                    break;
            }
        }
    }

    private void EmitWrappedHead(Tracker head, Tracker? below)
    {
        // A long line wraps (#2): after the bracketed name — `You have
        // obtained [Secret Book of the Forgotten Adventurer]` then
        // `x4. (18:51)` — or mid-name — `You have obtained [Deep Tide-Dyed
        // Standardized Timber` then `Square] x4. (20:25)` as the next visual
        // line. Wait for the tail to settle.
        if (below is null || Math.Abs(below.Y - head.Y) > 1.8 * _lineHeight)
        {
            // Nothing below (yet) — the tail may still be rendering. The
            // scroll-off skip in DropDepartedTrackers is the deadline.
            return;
        }
        if (below.SettledText is null && !below.Emitted) return; // tail still gathering consensus

        var tailKind = below is { Emitted: false, SettledText: not null } ? below.Settled.Kind : LootParser.Kind.Unrecognized;

        if (head.Settled.Kind == LootParser.Kind.NameOpen)
        {
            if (tailKind == LootParser.Kind.NameTail)
            {
                head.Emitted = true;
                below.Emitted = true;
                // The game wraps at a word boundary; the halves rejoin with
                // the one space the wrap swallowed. Both halves ship raw.
                var name = $"{head.Settled.Name} {below.Settled.Name}".Trim();
                if (name.Length == 0)
                    note($"skip  \"{head.SettledText} ⏎ {below.SettledText}\" — a wrapped name that read as nothing");
                else if (name == "Silver")
                    note($"skip  \"{head.SettledText} ⏎ {below.SettledText}\" — silver is currency, not sent");
                else
                    emit(name, below.Settled.Count, $"{head.SettledText} ⏎ {below.SettledText}");
            }
            else
            {
                // Only a NameTail can finish an open bracket. Anything else
                // below means the rest of this name is unrecoverable — and a
                // knowingly incomplete name is never sent.
                head.Emitted = true;
                note($"skip  \"{head.SettledText}\" — wrapped name whose ending never arrived");
            }
            return;
        }

        switch (tailKind)
        {
            case LootParser.Kind.QuantityTail:
                head.Emitted = true;
                below.Emitted = true;
                emit(head.Settled.Name, below.Settled.Count, $"{head.SettledText} ⏎ {below.SettledText}");
                break;
            case LootParser.Kind.TimestampTail:
                head.Emitted = true;
                below.Emitted = true;
                emit(head.Settled.Name, 1, $"{head.SettledText} ⏎ {below.SettledText}");
                break;
            default:
                // The next line is a full message of its own — this head's
                // tail is unrecoverable and its count unknowable. Skip aloud
                // rather than invent a count.
                head.Emitted = true;
                note($"skip  \"{head.SettledText}\" — wrapped line whose quantity never arrived");
                break;
        }
    }

    private void ResetForRealign(string reason)
    {
        var unconfirmed = _trackers.Count(t => !t.Emitted);
        _trackers.Clear();
        _lastLines.Clear();
        _baselinePending = true;
        _nullPasses = 0;
        _backwardsPasses = 0;
        note(unconfirmed > 0
            ? $"Realigning ({reason}) — {unconfirmed} unconfirmed line(s) skipped; what is on screen now is treated as old."
            : $"Realigning ({reason}) — what is on screen now is treated as old.");
    }

    private static string ModalReading(Tracker t)
    {
        var best = "";
        var bestCount = -1;
        foreach (var r in t.Readings)
            if (r.Value > bestCount) { best = r.Key; bestCount = r.Value; }
        return best;
    }

    private static double MedianHeight(List<OcrLineInput> lines)
    {
        var heights = lines.Select(l => l.Height).Order().ToArray();
        return heights[heights.Length / 2];
    }
}
