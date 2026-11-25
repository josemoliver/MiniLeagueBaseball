using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Animation;

// Bunts, and the running that goes with a hit and run.
//
// A bunt reuses most of the ground-ball staging (the fielder charges, fields it, throws): what differs is the batter's
// stance (see SquareToBunt in PlayChoreographer.cs), where the ball dies (BuntLanding) and how slowly it rolls (RollArrival).
// Sacrifices, bunts thrown out, bunts popped up and bunts booted go through GroundOut, CaughtBall and ErrorPlay with
// those three things changed; a bunt single and a fielder's choice are their own plays, below.
internal sealed partial class Staging
{
    // Where a bunt stops: just in front of the plate, in the grass, or down a line. Whoever fields it charges in.
    private FieldPoint BuntLanding(Position pos) => pos switch
    {
        Position.C => new FieldPoint(Between(-6, 6), Between(7, 13)),
        Position.P => FieldPoint.FromPolar(Between(-14, 14), Between(34, 46)),
        Position.First => FieldPoint.FromPolar(Between(20, 34), Between(30, 48)),
        Position.Third => FieldPoint.FromPolar(-Between(20, 34), Between(30, 48)),
        // The other infielders do not field bunts, but a result can name any of them: let him come in for it.
        _ => FieldPoint.FromPolar(Math.Clamp(_spots[pos].AngleDegrees, -30, 30), Between(38, 50)),
    };

    // When a ground ball reaches its spot. A bunt is a slow roller: the ball is deadened and takes its time.
    private double RollArrival(FieldPoint at) =>
        Contact + (_result.IsBunt ? 0.25 + at.DistanceFromHome / 55 : 0.3 + at.DistanceFromHome / 300);

    // ── A hit and run: the runner is going ───────────────────────────────────

    // He takes his lead as the pitcher winds up, then breaks for second as the ball is released and is well on his way
    // when it reaches the plate. What happens next (a hit that sends him on, or a ball he has to come back from) is
    // staged by the play itself; a runner who has to come back is returned to his base at the end (ReturnToBases).
    //
    // The same break is a suicide squeeze, from third: the runner is on his way to the plate as the pitch is delivered.
    private void RunnerBreaks(int fromBase = 1)
    {
        var goer = _result.RunnerMoves.FirstOrDefault(m => m.FromBase == fromBase);
        if (goer is null) return;

        TakeLeads();
        string id = ActorId(goer);
        double go = Math.Max(Release, _s.TimeOf(id));
        _s.Move(id, go, _s.PointOf(id), pose: Poses.Run);
        var start = FieldGeometry.BaseSpot(fromBase);
        var ahead = fromBase == 3 ? FieldGeometry.Home : FieldGeometry.Second;
        _s.Move(id, Math.Max(go + 0.05, Contact), start.Towards(ahead, 0.4), pose: Poses.Run);
        _s.Cue(Release, CueKind.Banner, fromBase == 3 ? "SQUEEZE PLAY!" : "HIT AND RUN!");
    }

    // ── A bunt for a hit ─────────────────────────────────────────────────────

    // The batter is away from the box at once and beats it out; the throw is made anyway and arrives late.
    private void BuntSingle()
    {
        var moves = _result.RunnerMoves;
        var batterMove = moves.First(m => m.FromBase == 0);
        var fielder = _result.Fielder is { } f && Has(f) ? f : Position.P;
        string id = IdOf(fielder);

        ContactPitch();
        var landing = GroundLanding(fielder);
        double tBall = RollArrival(landing);
        _s.Move("ball", tBall, landing, 0.5, ease: Ease.Roll);
        double tField = Fetch(fielder, landing, tBall, Poses.Crouch);
        if (tField > tBall) _s.Move("ball", tField, landing, 0.5);
        _s.Cue(tField, CueKind.Effect, "glove", landing);

        // Everyone runs on contact.
        var times = MoveAll(Contact + 0.1, SecondsPerBase, slide: SlidesHome, skip: m => m.FromBase == 0);
        var ran = MoveRunner(batterMove, Contact + 0.1, SecondsPerBase);
        double safeAt = ran.TryGetValue(1, out var t) ? t : Contact + 0.8;

        // WHY: the first baseman who fields it cannot throw to himself; he just holds it. Anyone else throws to first,
        // and the throw is slowed (or held) until just after the runner is on the bag, so the picture agrees with "safe".
        var first = FieldGeometry.First;
        string firstId = Has(Position.First) ? FielderId(Position.First) : "pitcher";
        double settled = tField + 0.25;
        if (id == firstId)
            Put(id, settled, landing, pose: Poses.Stand);
        else
        {
            var gloveSpot = firstId == FielderId(Position.First) ? new FieldPoint(first.X + 3.5, first.Y + 1) : first;
            settled = ThrowTo(id, gloveSpot, SetForRelay(id, gloveSpot, tField), arriveAfter: safeAt + 0.12);
            Receive(firstId, gloveSpot, settled, firstId == FielderId(Position.First) ? Poses.Reach : Poses.Catch);
            BackUpThrow(id, firstId, first, settled);
        }

        // WHY: the result is shown once the play is over: the throw has arrived and every runner has stopped.
        int batterIndex = -1;
        for (int i = 0; i < moves.Count; i++) if (ReferenceEquals(moves[i], batterMove)) batterIndex = i;
        if (batterIndex >= 0) times[batterIndex] = ran;
        _s.Cue(safeAt, CueKind.Sound, "steal");
        AnnounceHitAfterPlay(settled, moves, times);
        CheerForRuns(moves, times);
    }

    // ── A fielder's choice ───────────────────────────────────────────────────

    // The fielder takes the ball and throws to the next base ahead of the lead runner instead of to first: the runner
    // is out there and the batter, who had a head start, is safe at first.
    private void FieldersChoicePlay()
    {
        var moves = _result.RunnerMoves;
        var batterMove = moves.FirstOrDefault(m => m.FromBase == 0);
        var outMove = moves.FirstOrDefault(m => m.IsOut && m.FromBase > 0);
        if (batterMove is null || outMove is null)
        {
            GroundOut();   // not a play this can show: fall back to a plain ground ball
            return;
        }

        var fielder = _result.Fielder is { } f && Has(f) ? f : Position.P;
        string id = IdOf(fielder);

        ContactPitch();
        var landing = GroundLanding(fielder);
        double tBall = RollArrival(landing);
        _s.Move("ball", tBall, landing, 0.5, ease: Ease.Roll);
        double tField = Fetch(fielder, landing, tBall, Poses.Crouch);
        if (tField > tBall) _s.Move("ball", tField, landing, 0.5);
        _s.Cue(tField, CueKind.Effect, "glove", landing);

        // The other runners go with the contact; the one who is out is slowed so that the throw beats him.
        var times = MoveAll(Contact + 0.1, SecondsPerBase, slide: SlidesHome, skip: m => m == batterMove || m == outMove);

        // The infielder covering the bag takes the throw with his glove on the infield side of it. Out at the plate (a squeeze the
        // defense read), the catcher takes it; if the catcher fielded the bunt himself he just puts the tag on.
        bool atPlate = outMove.ToBase == 4;
        var bag = FieldGeometry.BaseSpot(outMove.ToBase);
        string cover = atPlate ? "catcher" : CoverFor(outMove.ToBase, id);
        var glove = atPlate ? HomeCatcher : new FieldPoint(bag.X + (bag.X < 0 ? 3 : -3), bag.Y - 1);
        double arrive;
        if (cover == id)
        {
            arrive = tField + 0.25;
            Put(id, arrive, glove, pose: Poses.Tag);
        }
        else
        {
            arrive = ThrowTo(id, glove, SetForRelay(id, glove, tField));
            Receive(cover, glove, arrive);
            Put(cover, arrive + 0.08, glove, pose: Poses.Tag);
        }

        MoveRunner(outMove, Contact + 0.1, SecondsPerBase, minArrival: arrive + 0.12, slide: true);
        MoveRunner(batterMove, Contact + 0.1, SecondsPerBase);

        _s.Cue(arrive, CueKind.Sound, "out");
        _s.Cue(arrive + 0.05, CueKind.Banner, _result.Description);
        CheerForRuns(moves, times);
    }

    // Who takes a throw at a base: the shortstop (or second baseman) at second, the third baseman at third, and if that is
    // the man who threw it, the shortstop. Falls back to the pitcher when the lineup has nobody there.
    private string CoverFor(int bas, string throwerId)
    {
        Position[] candidates = bas == 3
            ? [Position.Third, Position.Short]
            : [Position.Short, Position.Second];
        foreach (var pos in candidates)
            if (Has(pos) && IdOf(pos) != throwerId) return IdOf(pos);
        return "pitcher";
    }

    // ── A line drive that doubles off the runner ─────────────────────────────

    // The ball is caught on a line and the runner, who was going, has to turn around and get back: the throw to first
    // gets there ahead of him. The runner stays where the hit and run left him until the catch, then runs back.
    private void DoubleOffAtFirst(RunnerMove runner, string catcherId, double caughtAt) => DoubleOffAt(1, runner, catcherId, caughtAt);

    // The same turn at third, for the runner of a suicide squeeze whose bunt is popped up: he is on his way home, the fielder who
    // catches it throws to the third baseman, and he dives back too late.
    private void DoubleOffAt(int bas, RunnerMove runner, string catcherId, double caughtAt)
    {
        string id = ActorId(runner);
        var first = FieldGeometry.BaseSpot(bas);
        var coverPos = bas == 3 ? Position.Third : Position.First;
        string firstId = Has(coverPos) ? FielderId(coverPos) : "pitcher";

        var mid = _s.PointOf(id).Towards(bas == 3 ? FieldGeometry.Home : FieldGeometry.Second, 0.15);
        Put(id, caughtAt, mid, pose: Poses.Run);

        // The catcher of the line drive turns and throws across to first.
        var gloveSpot = firstId == FielderId(coverPos) ? new FieldPoint(first.X + (bas == 3 ? -3.5 : 3.5), first.Y + 1) : first;
        double arrive = catcherId == firstId
            ? caughtAt + 0.25
            : ThrowTo(catcherId, gloveSpot, SetForRelay(catcherId, gloveSpot, caughtAt));
        if (catcherId != firstId)
            Receive(firstId, gloveSpot, arrive, firstId == FielderId(coverPos) ? Poses.Reach : Poses.Catch);
        else
        {
            // WHY: the first baseman caught it himself; he carries the ball to the bag, so it must go with him.
            Put(firstId, arrive, first, pose: Poses.Tag);
            _s.Move("ball", arrive, first, 1.5);
        }

        // He turns on the spot, runs back and dives for the bag a beat after the ball is there.
        double back = Math.Max(arrive + 0.12, caughtAt + 0.1 + mid.DistanceTo(first) / 70);
        Put(id, caughtAt + 0.1, mid, pose: Poses.Run);
        Put(id, back - 0.3, _s.PointOf(id).Towards(first, 0.7), pose: Poses.Slide);
        Put(id, back, first, pose: Poses.Slide);
        _s.EndPose(id, Poses.Out);
        _s.Cue(Math.Max(0, back - 0.25), CueKind.Effect, "focus", first);
        _s.Cue(back, CueKind.Effect, "call", first);
        _s.Cue(back, CueKind.Hold, "0.22");
        _s.Cue(back, CueKind.Sound, "caught");
    }
}
