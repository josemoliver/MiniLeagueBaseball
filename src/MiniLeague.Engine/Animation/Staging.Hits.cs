using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Animation;

// Singles, doubles and triples.
internal sealed partial class Staging
{
    private enum Kind { GroundThrough, InfieldHit, Bloop, LineDrive, ShortHop }

    // A runner scoring sometimes slides; a batter taking an extra base slides into it.
    private bool SlidesHome(RunnerMove m) => m.Fate == RunnerFate.Scored && m.FromBase > 0 && _rng.NextDouble() < 0.4;

    private const double HomePlaySecondsPerBase = 1.18;

    // WHY: A runner coming from first or second needs time to round third while the outfielder fields and throws.
    // The closer arrival also lets the decided safe result read as a real play at the plate.
    private Dictionary<int, double>[] MoveHitRunners(Func<RunnerMove, bool> otherSlide, RunnerMove? sentOut = null)
    {
        var moves = _result.RunnerMoves;
        var times = new Dictionary<int, double>[moves.Count];
        double thirdClearAt = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            // NOTE: a runner thrown out is moved later, by TagOut, once the throw's arrival is known.
            if (ReferenceEquals(move, sentOut))
            {
                times[i] = new Dictionary<int, double>();
                continue;
            }

            bool slowToHome = move.Fate == RunnerFate.Scored && move.FromBase <= 2;
            bool endsAtThird = move.ToBase == 3 && move.Fate == RunnerFate.Safe;
            // WHY: the lead runner may round third slowly on a play at the plate. Keep a trailing
            // runner from reaching and standing on third until the lead runner is clear of the bag.
            double? thirdArrival = endsAtThird && thirdClearAt > 0 ? thirdClearAt + 0.3 : null;
            times[i] = MoveRunner(move, Contact + 0.1,
                slowToHome ? HomePlaySecondsPerBase : SecondsPerBase,
                minArrival: thirdArrival, slide: slowToHome || otherSlide(move));
            if (move.Fate == RunnerFate.Scored && times[i].TryGetValue(3, out var passedThird))
                thirdClearAt = Math.Max(thirdClearAt, passedThird);
        }
        return times;
    }

    // ── Single ───────────────────────────────────────────────────────────────

    private void Single()
    {
        ContactPitch();

        var kind = ChooseKind();
        var (fielder, landing) = ChooseLanding(kind);
        double tBall = SingleBallPath(kind, landing);

        // The fielder gets to the ball (it waits for him if he is slow).
        string id = IdOf(fielder);
        // WHY: even a bloop has to land before the fielder picks it up; catching it in the air would be an out.
        double tField = Fetch(fielder, landing, tBall, Poses.Crouch);
        if (tField > tBall) _s.Move("ball", tField, landing, 0.5);
        _s.Cue(tField, CueKind.Effect, "glove", landing);
        SupportOutfielder(fielder, landing, tField);

        // Runners go on contact.
        var moves = _result.RunnerMoves;
        var sentOut = SentOut(moves);
        var times = MoveHitRunners(SlidesHome, sentOut);

        // Where the throw goes, and when it has to arrive for the runner to be safe (or to beat the one sent hard).
        var (receiver, receiverPoint, arriveAfter) = sentOut is not null
            ? SentOutTarget(sentOut, id)
            : ChooseThrowTarget(kind, fielder, moves, times);
        if (receiver == id) (receiver, receiverPoint, arriveAfter) = ("pitcher", FieldGeometry.Mound, null);
        double arrive = ThrowTo(id, receiverPoint, tField + 0.25, arriveAfter);
        // WHY: TagOut times a thrown-out runner's actual arrival; the catcher's usual swipe would be early.
        Receive(receiver, receiverPoint, arrive, facing: receiver == "catcher" ? (landing.X < 0 ? "left" : "right") : null,
            tagAtHome: sentOut?.ToBase != 4);
        BackUpThrow(id, receiver, receiverPoint, arrive);
        double? plateCall = sentOut is null ? ClosePlayAtHome(receiver, arrive, moves, times) : null;
        if (sentOut is not null) TagOut(sentOut, receiver, receiverPoint, arrive, moves, times);

        _s.Cue(Contact, CueKind.Sound, "hit");
        _s.Cue(Contact + 0.12, CueKind.Banner, kind switch
        {
            Kind.Bloop => "SOFT FLY BALL...",
            Kind.LineDrive => "LINE DRIVE...",
            Kind.ShortHop => "SHARP HOP TO THE INFIELD...",
            Kind.InfieldHit => "SLOW ROLLER...",
            _ => "GROUND BALL IN PLAY...",
        });
        AnnounceHitAfterPlay(arrive, moves, times, plateCall);
        CheerForRuns(moves, times);
    }

    private void AnnounceHitAfterPlay(double throwArrival, IReadOnlyList<RunnerMove> moves,
        Dictionary<int, double>[] times, double? plateCall = null)
    {
        double resolved = Math.Max(throwArrival, plateCall ?? 0);
        for (int i = 0; i < moves.Count; i++)
            if (times[i].Count > 0) resolved = Math.Max(resolved, times[i].Values.Max());
        _s.Cue(resolved + 0.08, CueKind.Banner, _result.Description);
        _s.Cue(resolved + 0.08, CueKind.Hold, "0.55");
    }

    // WHY: the runner is safe when he touches home, but the visible call waits for the catcher to receive and swipe.
    private double? ClosePlayAtHome(string receiver, double throwArrival,
        IReadOnlyList<RunnerMove> moves, Dictionary<int, double>[] times)
    {
        if (receiver != "catcher") return null;
        var crossings = moves.Select((m, i) => (Move: m, Index: i))
            .Where(x => x.Move.Fate == RunnerFate.Scored && x.Move.FromBase <= 2
                && times[x.Index].ContainsKey(4))
            .Select(x => times[x.Index][4]).ToList();
        if (crossings.Count == 0) return null;
        double crossed = crossings.Max();
        if (throwArrival - crossed > 0.85) return null;

        double call = throwArrival + 0.24;
        _s.Cue(Math.Max(Contact + 0.15, crossed - 0.38), CueKind.Banner, "HERE COMES THE THROW!");
        _s.Cue(call, CueKind.Effect, "platecall", FieldGeometry.Home);
        _s.Cue(call, CueKind.Banner, "SAFE AT HOME!");
        _s.Cue(call, CueKind.Hold, "0.3");
        return call;
    }

    private void CheerForRuns(IReadOnlyList<RunnerMove> moves, Dictionary<int, double>[] times)
    {
        for (int i = 0; i < moves.Count; i++)
            if (moves[i].Fate == RunnerFate.Scored && times[i].TryGetValue(4, out var t))
                _s.Cue(t, CueKind.Sound, "cheer");
    }

    private Kind ChooseKind()
    {
        double roll = _rng.NextDouble();
        bool canInfield = Has(Position.Second) || Has(Position.Short) || Has(Position.Third);
        if (roll < 0.16) return canInfield ? Kind.ShortHop : Kind.GroundThrough;
        if (roll < 0.30) return canInfield ? Kind.InfieldHit : Kind.LineDrive;
        if (roll < 0.58) return Kind.GroundThrough;
        return roll < 0.80 ? Kind.LineDrive : Kind.Bloop;
    }

    // A single may roll through, drop softly, skip off the grass, or short-hop an infielder.
    // Every path reaches the ground before the fielder picks it up; only the picture varies.
    private double SingleBallPath(Kind kind, FieldPoint landing)
    {
        double distance = landing.DistanceFromHome;
        if (kind is Kind.GroundThrough or Kind.InfieldHit)
        {
            double arrive = Contact + 0.3 + distance / 260;
            _s.Move("ball", arrive, landing, 0.5, ease: Ease.Roll);
            return arrive;
        }

        // WHY: the extra key before the pickup shows the ball touching the grass. A low liner
        // skids onward, a soft fly trickles, and a short hop lifts briefly before the glove.
        double shortOfFielder = kind == Kind.ShortHop ? 8 : kind == Kind.LineDrive ? 14 : 6;
        var firstBounce = FieldPoint.FromPolar(landing.AngleDegrees, Math.Max(12, distance - shortOfFielder));
        double bounce = kind switch
        {
            Kind.ShortHop => Contact + 0.22 + distance / 350,
            Kind.LineDrive => Contact + 0.22 + distance / 380,
            _ => Contact + 0.4 + distance / 260,
        };
        double apex = kind switch
        {
            Kind.ShortHop => 5,
            Kind.LineDrive => Math.Clamp(distance * 0.08, 8, 18),
            _ => Math.Clamp(distance * 0.18, 12, 45),
        };
        _s.Move("ball", bounce, firstBounce, 0.5, ease: Ease.Arc, apex: apex);
        _s.Cue(bounce, CueKind.Effect, "bounce", firstBounce);
        double pickedUp = bounce + (kind == Kind.Bloop ? 0.2 : 0.16);
        _s.Move("ball", pickedUp, landing, 0.5,
            ease: kind == Kind.ShortHop ? Ease.Arc : Ease.Roll,
            apex: kind == Kind.ShortHop ? 2.5 : 0);
        return pickedUp;
    }

    // Picks who fields it first, then drops the ball where he can reach it.
    private (Position Fielder, FieldPoint Landing) ChooseLanding(Kind kind)
    {
        var candidates = ((kind is Kind.InfieldHit or Kind.ShortHop)
                ? new[] { Position.Second, Position.Short, Position.Third }
                : FieldGeometry.Outfielders)
            .Where(Has).ToList();
        if (candidates.Count == 0) candidates = FieldGeometry.FieldingPositions.Where(Has).ToList();

        var fielder = PickByPull(candidates);
        var spot = _spots[fielder];
        double angle = spot.AngleDegrees + Between(-8, 8);
        double depth = kind switch
        {
            Kind.InfieldHit => spot.DistanceFromHome + Between(-10, 12),
            Kind.ShortHop => spot.DistanceFromHome + Between(-14, 8),
            Kind.GroundThrough => spot.DistanceFromHome - Between(10, 35),
            Kind.LineDrive => spot.DistanceFromHome - Between(18, 38),
            _ => spot.DistanceFromHome - Between(18, 45),
        };
        var landing = FieldPoint.FromPolar(Math.Clamp(angle, -43, 43), Math.Max(depth, 30));
        return (fielder, landing);
    }

    // Home if somebody scored, first on an infield hit, else the base the lead runner took, else the pitcher.
    private (string Receiver, FieldPoint Point, double? ArriveAfter) ChooseThrowTarget(
        Kind kind, Position fielder, IReadOnlyList<RunnerMove> moves, Dictionary<int, double>[] times)
    {
        if (moves.Any(m => m.Fate == RunnerFate.Scored))
            return ("catcher", new FieldPoint(0, -2), HomeThrowAfter(moves, times));

        if ((kind is Kind.InfieldHit or Kind.ShortHop) && fielder != Position.First && Has(Position.First))
            return (FielderId(Position.First), FieldGeometry.First, LatestAt(moves, times, 1, m => m.FromBase == 0));

        int lead = moves.Where(m => m.FromBase > 0 && m.Fate == RunnerFate.Safe && m.ToBase > m.FromBase)
                        .Select(m => m.ToBase).DefaultIfEmpty(0).Max();
        var cover = lead == 3 ? Position.Third : lead == 2 ? Position.Second : (Position?)null;
        if (cover is { } pos && pos != fielder && Has(pos))
            return (FielderId(pos), FieldGeometry.BaseSpot(lead),
                    LatestAt(moves, times, lead, m => m.ToBase == lead && m.Fate == RunnerFate.Safe));

        return ("pitcher", FieldGeometry.Mound, null);
    }

    // WHY: A safe runner reaches the plate a visible beat before a throw that is trying to cut him down.
    private static double? HomeThrowAfter(IReadOnlyList<RunnerMove> moves, Dictionary<int, double>[] times) =>
        LatestAt(moves, times, 4, m => m.Fate == RunnerFate.Scored) is double home ? home + 0.18 : null;

    // ── A runner sent hard and thrown out ────────────────────────────────────

    // The runner (not the batter) the defense threw out on this hit, if any.
    private static RunnerMove? SentOut(IReadOnlyList<RunnerMove> moves) =>
        moves.FirstOrDefault(m => m.FromBase > 0 && m.IsOut);

    // The throw goes to the base he was sent to: the catcher at the plate, the third baseman at third (the shortstop
    // covers when the third baseman is the one throwing).
    private (string Receiver, FieldPoint Point, double? ArriveAfter) SentOutTarget(RunnerMove m, string thrower)
    {
        if (m.ToBase == 4) return ("catcher", new FieldPoint(0, -2), null);
        var cover = Has(Position.Third) && IdOf(Position.Third) != thrower ? Position.Third : Position.Short;
        return Has(cover) ? (IdOf(cover), FieldGeometry.BaseSpot(3), null) : ("pitcher", FieldGeometry.BaseSpot(3), null);
    }

    // WHY: the out is decided; the picture has to agree. The runner is timed to slide in just after the ball arrives,
    // the fielder puts the tag on, and the call is the out.
    private void TagOut(RunnerMove m, string receiver, FieldPoint bag, double throwArrival,
        IReadOnlyList<RunnerMove> moves, Dictionary<int, double>[] times)
    {
        double perBase = m.ToBase == 4 ? HomePlaySecondsPerBase : SecondsPerBase;
        var ran = MoveRunner(m, Contact + 0.1, perBase, minArrival: throwArrival + 0.14, slide: true);
        int index = -1;
        for (int i = 0; i < moves.Count; i++) if (ReferenceEquals(moves[i], m)) index = i;
        if (index >= 0) times[index] = ran;
        if (!ran.TryGetValue(m.ToBase, out double tagged)) return;
        // NOTE: the tag goes down as he arrives, a beat after the slide reaches the bag.
        Put(receiver, tagged + 0.05, bag, pose: Poses.Tag);
        _s.Cue(Math.Max(Contact + 0.15, tagged - 0.45), CueKind.Banner, "HERE COMES THE THROW!");
        _s.Cue(tagged, CueKind.Sound, "caught");
        _s.Cue(tagged + 0.05, CueKind.Banner, m.ToBase == 4 ? "OUT AT THE PLATE!" : "OUT AT THIRD!");
    }

    // ── Double and triple ────────────────────────────────────────────────────

    // The ball goes to the gap or down a line, rolls toward the wall, an outfielder runs it down and
    // relays it in through a cutoff man to the base where the play is.
    private void ExtraBaseHit(bool triple)
    {
        var park = _before.Stadium;
        var outfielders = FieldGeometry.Outfielders.Where(Has).ToList();
        if (outfielders.Count == 0)
        {
            Basic();
            return;
        }
        ContactPitch();

        var pos = PickByPull(outfielders);
        var spot = _spots[pos];
        // Down a line (for the corner outfielders) or into a gap.
        double angle = pos != Position.Center && _rng.NextDouble() < 0.25
            ? Math.Sign(spot.X) * Between(33, 42)
            : Math.Clamp(spot.AngleDegrees + Between(-14, 14), -40, 40);
        double landDepth = Between(0.72, 0.85), restBack = Between(4, 9);
        double wall = FieldGeometry.WallDistance(park, angle);
        var rest = FieldPoint.FromPolar(angle, wall - restBack);
        // Keep the chase short enough to be worth watching: if the ball went far from him, it went nearer.
        for (int i = 0; i < 4 && spot.DistanceTo(rest) > 95; i++)
        {
            angle = angle * 0.6 + spot.AngleDegrees * 0.4;
            wall = FieldGeometry.WallDistance(park, angle);
            rest = FieldPoint.FromPolar(angle, wall - restBack);
        }
        var landing = FieldPoint.FromPolar(angle, wall * landDepth);
        double tLand = Contact + 0.4 + landing.DistanceFromHome / 320;
        double tRest = tLand + 0.35 + (rest.DistanceFromHome - landing.DistanceFromHome) / 140 + (triple ? 0.25 : 0);
        _s.Move("ball", tLand, landing, 0, ease: Ease.Arc, apex: Math.Clamp(landing.DistanceFromHome * 0.25, 15, 55));
        // WHY: the ball runs on to the wall, hits it and comes back off it to where the outfielder picks it up, so the
        // wall plays a part. The rebound takes the last 0.15 s (tRest is at least 0.35 s after tLand, so it fits).
        _s.Move("ball", tRest - 0.15, FieldPoint.FromPolar(angle, wall), 1.2, ease: Ease.Roll);
        _s.Move("ball", tRest, rest, 0.5);

        string id = IdOf(pos);
        double tField = Fetch(pos, rest, tRest, Poses.Crouch);
        if (tField > tRest) _s.Move("ball", tField, rest, 0.5);
        _s.Cue(tField, CueKind.Effect, "glove", rest);
        SupportOutfielder(pos, rest, tField);

        // The runners go on contact; the batter slides into the base he takes.
        var moves = _result.RunnerMoves;
        var sentOut = SentOut(moves);
        var times = MoveHitRunners(m => m.FromBase == 0 && m.ToBase >= 2 || SlidesHome(m), sentOut);

        // Relay: the shortstop takes it on the left side, the second baseman on the right.
        var cutPos = rest.X < 0 ? Position.Short : Position.Second;
        if (!Has(cutPos)) cutPos = cutPos == Position.Short ? Position.Second : Position.Short;
        var (target, targetPoint, arriveAfter) = sentOut is not null
            ? SentOutTarget(sentOut, IdOf(cutPos))
            : ChooseRelayTarget(moves, times, cutPos);

        double arrive;
        string finalThrower = id;
        if (Has(cutPos) && cutPos != Position.P)
        {
            string cut = IdOf(cutPos);
            var cutPoint = FieldPoint.FromPolar(angle * 0.7, 140);
            double arrive1 = ThrowTo(id, cutPoint, tField + Transfer);
            Receive(cut, cutPoint, arrive1);
            arrive = ThrowTo(cut, targetPoint, SetForRelay(cut, targetPoint, arrive1), arriveAfter);
            finalThrower = cut;
        }
        else
            arrive = ThrowTo(id, targetPoint, tField + Transfer, arriveAfter);
        Receive(target, targetPoint, arrive, facing: target == "catcher" ? (rest.X < 0 ? "left" : "right") : null,
            tagAtHome: sentOut?.ToBase != 4);
        BackUpThrow(finalThrower, target, targetPoint, arrive);
        double? plateCall = sentOut is null ? ClosePlayAtHome(target, arrive, moves, times) : null;
        if (sentOut is not null) TagOut(sentOut, target, targetPoint, arrive, moves, times);

        _s.Cue(Contact, CueKind.Sound, "hit");
        _s.Cue(Contact + 0.12, CueKind.Banner, "DEEP TO THE OUTFIELD...");
        AnnounceHitAfterPlay(arrive, moves, times, plateCall);
        CheerForRuns(moves, times);
    }

    // Home if somebody scored; else the base the lead runner (the batter included) took.
    private (string Receiver, FieldPoint Point, double? ArriveAfter) ChooseRelayTarget(
        IReadOnlyList<RunnerMove> moves, Dictionary<int, double>[] times, Position cutPos)
    {
        if (moves.Any(m => m.Fate == RunnerFate.Scored))
            return ("catcher", new FieldPoint(0, -2), HomeThrowAfter(moves, times));

        int lead = moves.Where(m => m.Fate == RunnerFate.Safe && m.ToBase > m.FromBase)
                        .Select(m => m.ToBase).DefaultIfEmpty(0).Max();
        if (lead < 2) return ("pitcher", FieldGeometry.Mound, null);

        var cover = lead == 3 ? Position.Third : cutPos == Position.Second ? Position.Short : Position.Second;
        string receiver = Has(cover) && cover != cutPos ? IdOf(cover) : "pitcher";
        var point = receiver == "pitcher" ? FieldGeometry.Mound : FieldGeometry.BaseSpot(lead);
        return (receiver, point, LatestAt(moves, times, lead, m => m.ToBase == lead && m.Fate == RunnerFate.Safe));
    }
}
