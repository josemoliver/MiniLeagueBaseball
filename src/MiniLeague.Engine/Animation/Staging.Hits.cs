using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Animation;

// Singles, doubles and triples.
internal sealed partial class Staging
{
    private enum Kind { GroundThrough, InfieldHit, Bloop }

    // A runner scoring sometimes slides; a batter taking an extra base slides into it.
    private bool SlidesHome(RunnerMove m) => m.Fate == RunnerFate.Scored && m.FromBase > 0 && _rng.NextDouble() < 0.4;

    private const double HomePlaySecondsPerBase = 1.18;

    // WHY: A runner coming from first or second needs time to round third while the outfielder fields and throws.
    // The closer arrival also lets the decided safe result read as a real play at the plate.
    private Dictionary<int, double>[] MoveHitRunners(Func<RunnerMove, bool> otherSlide)
    {
        var moves = _result.RunnerMoves;
        var times = MoveAll(Contact + 0.1, SecondsPerBase, slide: otherSlide,
            skip: m => m.Fate == RunnerFate.Scored && m.FromBase <= 2);
        for (int i = 0; i < moves.Count; i++)
            if (moves[i].Fate == RunnerFate.Scored && moves[i].FromBase <= 2)
                times[i] = MoveRunner(moves[i], Contact + 0.1, HomePlaySecondsPerBase, slide: true);
        return times;
    }

    // ── Single ───────────────────────────────────────────────────────────────

    private void Single()
    {
        ContactPitch();

        var kind = ChooseKind();
        var (fielder, landing) = ChooseLanding(kind);
        double distance = landing.DistanceFromHome;

        // The ball goes down (and rolls) or up (and comes down).
        double tBall = kind == Kind.Bloop ? Contact + 0.4 + distance / 260 : Contact + 0.3 + distance / 260;
        if (kind == Kind.Bloop)
            _s.Move("ball", tBall, landing, 0, ease: Ease.Arc, apex: Math.Clamp(distance * 0.18, 12, 45));
        else
            _s.Move("ball", tBall, landing, 0.5, ease: Ease.Roll);

        // The fielder gets to the ball (it waits for him if he is slow).
        string id = IdOf(fielder);
        // A ball that rolls is fielded crouching with the glove at the grass; a bloop is taken with the glove up.
        double tField = Fetch(fielder, landing, tBall, kind == Kind.Bloop ? Poses.Catch : Poses.Crouch);
        if (tField > tBall) _s.Move("ball", tField, landing, 0.5);
        _s.Cue(tField, CueKind.Effect, "glove", landing);
        SupportOutfielder(fielder, landing, tField);

        // Runners go on contact.
        var moves = _result.RunnerMoves;
        var times = MoveHitRunners(SlidesHome);

        // Where the throw goes, and when it has to arrive for the runner to be safe.
        var (receiver, receiverPoint, arriveAfter) = ChooseThrowTarget(kind, fielder, moves, times);
        if (receiver == id) (receiver, receiverPoint, arriveAfter) = ("pitcher", FieldGeometry.Mound, null);
        double arrive = ThrowTo(id, receiverPoint, tField + 0.25, arriveAfter);
        Receive(receiver, receiverPoint, arrive, facing: receiver == "catcher" ? (landing.X < 0 ? "left" : "right") : null);
        BackUpThrow(id, receiver, receiverPoint, arrive);
        double? plateCall = ClosePlayAtHome(receiver, arrive, moves, times);

        _s.Cue(Contact, CueKind.Sound, "hit");
        _s.Cue(Contact + 0.12, CueKind.Banner, kind == Kind.Bloop ? "BALL IN THE AIR..." : "GROUND BALL IN PLAY...");
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
        if (roll < 0.20 && canInfield) return Kind.InfieldHit;
        return roll < 0.60 ? Kind.GroundThrough : Kind.Bloop;
    }

    // Picks who fields it first, then drops the ball where he can reach it.
    private (Position Fielder, FieldPoint Landing) ChooseLanding(Kind kind)
    {
        var candidates = (kind == Kind.InfieldHit
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
            Kind.GroundThrough => spot.DistanceFromHome - Between(10, 35),
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

        if (kind == Kind.InfieldHit && fielder != Position.First && Has(Position.First))
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
        var times = MoveHitRunners(m => m.FromBase == 0 && m.ToBase >= 2 || SlidesHome(m));

        // Relay: the shortstop takes it on the left side, the second baseman on the right.
        var cutPos = rest.X < 0 ? Position.Short : Position.Second;
        if (!Has(cutPos)) cutPos = cutPos == Position.Short ? Position.Second : Position.Short;
        var (target, targetPoint, arriveAfter) = ChooseRelayTarget(moves, times, cutPos);

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
        Receive(target, targetPoint, arrive, facing: target == "catcher" ? (rest.X < 0 ? "left" : "right") : null);
        BackUpThrow(finalThrower, target, targetPoint, arrive);
        double? plateCall = ClosePlayAtHome(target, arrive, moves, times);

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
