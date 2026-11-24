using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Animation;

// Ground outs, double plays, balls caught in the air, and errors.
//
// For an out the ball is thrown at fastest arcade speed and the runner is slowed (or held back a moment) so
// that the ball gets there first: the result is already decided, so the picture is made to agree with it.
internal sealed partial class Staging
{
    private enum Catch { Fly, Line, Pop }

    private static readonly FieldPoint HomeCatcher = new(0, -2);

    // Who fields a ground ball (same odds as the sim uses, roughly).
    private Position PickGroundFielder()
    {
        (Position Pos, double Share)[] table =
            [(Position.Short, 0.30), (Position.Second, 0.21), (Position.Third, 0.20),
             (Position.First, 0.17), (Position.P, 0.08), (Position.C, 0.04)];
        var present = table.Where(t => Has(t.Pos)).ToList();
        double roll = _rng.NextDouble() * present.Sum(t => t.Share);
        foreach (var (pos, share) in present)
            if ((roll -= share) < 0) return pos;
        return present[^1].Pos;
    }

    // Who catches it in the air: outfielders on a fly, infielders on a line drive or pop-up.
    private Position PickCatcher(Catch kind)
    {
        if (kind == Catch.Pop)
        {
            // WHY: A pop-up can hang around the plate; the battery and corner fielders must be eligible to take it.
            (Position Pos, double Share)[] table =
                [(Position.C, 0.24), (Position.P, 0.12), (Position.First, 0.21),
                 (Position.Third, 0.21), (Position.Second, 0.11), (Position.Short, 0.11)];
            var present = table.Where(t => Has(t.Pos)).ToList();
            double roll = _rng.NextDouble() * present.Sum(t => t.Share);
            foreach (var (pos, share) in present)
                if ((roll -= share) < 0) return pos;
            return present[^1].Pos;
        }
        var candidates = (kind == Catch.Fly ? FieldGeometry.Outfielders : FieldGeometry.Infielders).Where(Has).ToList();
        if (candidates.Count == 0) candidates = FieldGeometry.FieldingPositions.Where(Has).ToList();
        return candidates[_rng.Next(candidates.Count)];
    }

    // Where a ground ball is fielded: in front of the fielder, who may have charged it.
    private FieldPoint GroundLanding(Position pos)
    {
        if (_result.IsBunt) return BuntLanding(pos);
        var spot = _spots[pos];
        return pos switch
        {
            Position.C => new FieldPoint(Between(-5, 5), Between(7, 14)),
            Position.P => new FieldPoint(spot.X + Between(-4, 4), spot.Y + Between(-6, 2)),
            _ => FieldPoint.FromPolar(Math.Clamp(spot.AngleDegrees + Between(-6, 6), -43, 43),
                                      Math.Max(spot.DistanceFromHome - Between(0, 12), 25)),
        };
    }

    // Where a ball in the air comes down, near the fielder.
    private FieldPoint CatchPoint(Position pos, Catch kind)
    {
        var spot = _spots[pos];
        if (pos == Position.C) return new FieldPoint(Between(-7, 7), Between(-12, -6));   // a foul pop behind the plate
        if (pos == Position.P) return new FieldPoint(spot.X + Between(-5, 5), spot.Y + Between(-6, 6));
        if (pos is Position.Left or Position.Center or Position.Right)
        {
            double angle = Math.Clamp(spot.AngleDegrees + Between(-6, 6), -43, 43);
            double limit = FieldGeometry.WallDistance(_before.Stadium, angle) - 8;
            return FieldPoint.FromPolar(angle, Math.Clamp(spot.DistanceFromHome + Between(-12, 8), 60, limit));
        }
        var near = new FieldPoint(spot.X + Between(-5, 5), spot.Y + Between(-4, 8));
        return FieldGeometry.IsFair(near) ? near : spot;
    }

    // A high pop by the plate may drift behind the catcher or into foul ground near a corner fielder.
    private FieldPoint PlatePopPoint(Position pos) => pos switch
    {
        Position.C => new FieldPoint((_rng.NextDouble() < 0.5 ? -1 : 1) * Between(8, 14), -Between(7, 10)),
        Position.P => new FieldPoint(Between(-9, 9), Between(18, 30)),
        Position.First => new FieldPoint(Between(25, 36), Between(9, 20)),
        Position.Third => new FieldPoint(-Between(25, 36), Between(9, 20)),
        _ => CatchPoint(pos, Catch.Pop),
    };

    // ── Ground out ───────────────────────────────────────────────────────────

    private void GroundOut()
    {
        var moves = _result.RunnerMoves;
        var batterMove = moves.FirstOrDefault(m => m.FromBase == 0);
        var fielder = _result.Fielder is { } f && Has(f) ? f : PickGroundFielder();
        string id = IdOf(fielder);

        ContactPitch();
        var landing = GroundLanding(fielder);
        double tBall = RollArrival(landing);
        _s.Move("ball", tBall, landing, 0.5, ease: Ease.Roll);
        double tField = Fetch(fielder, landing, tBall, Poses.Crouch);
        if (tField > tBall) _s.Move("ball", tField, landing, 0.5);
        _s.Cue(tField, CueKind.Effect, "glove", landing);
        SupportOutfielder(fielder, landing, tField);

        // Runners already on base go with the contact.
        var times = MoveAll(Contact + 0.1, SecondsPerBase, slide: SlidesHome, skip: m => m.FromBase == 0);

        // Out at first: a throw to the first baseman, or the first baseman steps on the bag himself.
        var first = FieldGeometry.First;
        string firstId = Has(Position.First) ? FielderId(Position.First) : "pitcher";
        double arrive;
        if (fielder == Position.First || firstId == id)
        {
            arrive = tField + 0.1 + landing.DistanceTo(first) / FielderSpeed;
            Put(id, arrive, first, pose: Poses.Tag);
            Put("ball", arrive, first, 3);
        }
        else
        {
            var gloveSpot = firstId == FielderId(Position.First)
                ? new FieldPoint(first.X + 3.5, first.Y + 1) : first;
            arrive = ThrowTo(id, gloveSpot, SetForRelay(id, gloveSpot, tField));
            Receive(firstId, gloveSpot, arrive, firstId == FielderId(Position.First) ? Poses.Reach : Poses.Catch);
            BackUpThrow(id, firstId, first, arrive);
        }

        if (batterMove is not null)
            MoveRunner(batterMove, Contact + 0.1, SecondsPerBase, minArrival: arrive + 0.12);

        _s.Cue(arrive, CueKind.Sound, "out");
        _s.Cue(arrive + 0.05, CueKind.Banner, _result.Description);
        CheerForRuns(moves, times);
    }

    // ── Double play ──────────────────────────────────────────────────────────

    private void DoublePlay()
    {
        var moves = _result.RunnerMoves;
        var batterMove = moves.FirstOrDefault(m => m.FromBase == 0);
        var runnerOut = moves.FirstOrDefault(m => m.FromBase == 1 && m.IsOut);
        if (batterMove is null || runnerOut is null)
        {
            GroundOut();
            return;
        }

        var fielder = _result.Fielder is { } f && Has(f) ? f : Position.Short;
        string id = IdOf(fielder);

        // The middle infielder who is not fielding the ball takes the throw at second.
        var pivotPos = fielder == Position.Short ? Position.Second : fielder == Position.Second ? Position.Short
                     : _rng.NextDouble() < 0.5 ? Position.Short : Position.Second;
        if (!Has(pivotPos) || pivotPos == fielder)
            pivotPos = pivotPos == Position.Short ? Position.Second : Position.Short;
        string pivot = Has(pivotPos) ? IdOf(pivotPos) : "pitcher";

        ContactPitch();
        var landing = GroundLanding(fielder);
        double tBall = Contact + 0.3 + landing.DistanceFromHome / 300;
        _s.Move("ball", tBall, landing, 0.5, ease: Ease.Roll);
        double tField = Fetch(fielder, landing, tBall, Poses.Crouch);
        if (tField > tBall) _s.Move("ball", tField, landing, 0.5);
        _s.Cue(tField, CueKind.Effect, "glove", landing);
        SupportOutfielder(fielder, landing, tField);

        // The other runners go with the contact.
        var times = MoveAll(Contact + 0.1, SecondsPerBase, slide: SlidesHome, skip: m => m == batterMove || m == runnerOut);

        // To second, then on to first.
        var second = FieldGeometry.Second;
        var first = FieldGeometry.First;
        double arrive2 = ThrowTo(id, second, tField + Transfer);
        Receive(pivot, second, arrive2);
        _s.EndPose(pivot, Poses.Tag);

        string firstId = Has(Position.First) ? FielderId(Position.First) : "pitcher";
        // WHY: The pivot makes the call, turns and plants before sending the ball to first.
        double arrive1 = ThrowTo(pivot, first, SetForRelay(pivot, first, arrive2));
        Receive(firstId, first, arrive1);
        BackUpThrow(pivot, firstId, first, arrive1);

        // Both runners are out: each gets there just after the ball.
        MoveRunner(runnerOut, Contact + 0.1, SecondsPerBase, minArrival: arrive2 + 0.12, slide: true);
        MoveRunner(batterMove, Contact + 0.1, SecondsPerBase, minArrival: arrive1 + 0.12);

        _s.Cue(arrive2, CueKind.Sound, "out");
        _s.Cue(arrive1, CueKind.Sound, "doubleplay");
        _s.Cue(arrive1 + 0.05, CueKind.Banner, _result.Description);
        CheerForRuns(moves, times);
    }

    // ── A ball caught in the air: fly out, line out, pop out ─────────────────

    private void CaughtBall(Catch kind)
    {
        var pos = _result.Fielder is { } f && Has(f) ? f : PickCatcher(kind);
        string id = IdOf(pos);

        ContactPitch();
        bool platePop = kind == Catch.Pop && pos is Position.C or Position.P or Position.First or Position.Third;
        if (platePop)
        {
            var catcherSpot = _spots[Position.C];
            Put("catcher", Contact + 0.08, catcherSpot, pose: Poses.Pop);
            Put("catcher", Contact + 0.25, catcherSpot, pose: Poses.MaskOff);
            var maskSpot = new FieldPoint(_batterX < 0 ? 5 : -5, catcherSpot.Y - 1);
            _s.Cue(Contact + 0.4, CueKind.Effect, "mask", maskSpot);
            Put("catcher", Contact + 0.4, catcherSpot, pose: Poses.Track);
            _s.Cue(Contact + 0.25, CueKind.Banner, "HIGH POP-UP!");
        }
        // WHY: only an outfield fly out can become a wall catch; the result and the credited fielder stay the same.
        bool wallCatch = kind == Catch.Fly && pos is (Position.Left or Position.Center or Position.Right)
            && _rng.NextDouble() < 0.18;
        bool dive = !wallCatch && kind != Catch.Pop && pos is not (Position.C or Position.P)
            && _rng.NextDouble() < DiveChance(pos);
        var landing = wallCatch ? WallCatchPoint(pos)
            : platePop ? PlatePopPoint(pos) : dive ? DivePoint(pos, kind) : CatchPoint(pos, kind);
        double d = landing.DistanceFromHome;
        double flight = kind switch { Catch.Fly => 0.6 + d / 170, Catch.Pop => 2.4 + d / 350, _ => 0.3 + d / 320 };
        double apex = kind switch { Catch.Fly => Math.Clamp(d * 0.4, 28, 80), Catch.Pop => 55, _ => 6 + d * 0.05 };

        // The ball comes down when the fielder can be there.
        double go = Math.Max(Contact + 0.1, _s.TimeOf(id));
        double tCatch = Math.Max(Contact + flight, go + _s.PointOf(id).DistanceTo(landing) / FielderSpeed
            + (wallCatch ? 0.28 : 0));
        _s.Move("ball", tCatch, landing, wallCatch ? 8 : dive ? 1.5 : 4, ease: Ease.Arc, apex: apex);
        if (wallCatch)
        {
            Fetch(pos, landing, tCatch - 0.28, Poses.Track);
            Put(id, tCatch - 0.12, landing, z: 1.5, pose: Poses.Leap);
            Put(id, tCatch, landing, z: 3.5);
            Put(id, tCatch + 0.38, landing, pose: Poses.Catch);
        }
        else
            Fetch(pos, landing, tCatch, dive ? Poses.Dive : Poses.Catch);
        if (platePop)
        {
            if (pos == Position.C)
                _s.Move(id, Math.Max(tCatch, _s.TimeOf(id)), landing, pose: Poses.Catch, facing: "front");
            else
            {
                // WHY: The catcher clears the plate and follows the ball, then yields to the fielder making the catch.
                var from = _spots[Position.C];
                var backup = from.Towards(landing, 0.3);
                Put("catcher", Contact + 0.42, from, pose: Poses.Run);
                Put("catcher", tCatch - 0.22, backup, pose: Poses.Track);
            }
            if (pos != Position.P)
            {
                var from = _spots[Position.P];
                var backup = from.Towards(landing, 0.23);
                Put("pitcher", Contact + 0.2, from, pose: Poses.Run);
                Put("pitcher", tCatch - 0.25, backup, pose: Poses.Track);
            }
        }
        if (pos is Position.Left or Position.Center or Position.Right) SupportOutfielder(pos, landing, tCatch);
        if (wallCatch) _s.Cue(Math.Max(Contact + 0.25, tCatch - 0.65), CueKind.Banner, "BACK TO THE WALL...");
        _s.Cue(Math.Max(Contact + 0.1, tCatch - 0.45), CueKind.Effect, "focus", landing);
        _s.Cue(tCatch, CueKind.Effect, "glove", landing);
        if (wallCatch) _s.Cue(tCatch, CueKind.Effect, "wallcatch", landing);

        // The batter is out. A runner on third who scores tagged up and went after the catch.
        var moves = _result.RunnerMoves;
        var times = MoveAll(tCatch + 0.05, SecondsPerBase, slide: SlidesHome);

        // WHY: The fielder gathers himself on one knee before standing or transferring the ball to throw.
        if (dive) Put(id, tCatch + 0.42, landing, pose: Poses.Crouch);
        double ready = dive ? 0.75 : wallCatch ? 0.55 : Transfer;
        bool threw = false;
        if (moves.Any(m => m.Fate == RunnerFate.Scored) && id != "catcher")
        {
            double arrive = ThrowTo(id, HomeCatcher, tCatch + ready,
                LatestAt(moves, times, 4, m => m.Fate == RunnerFate.Scored));
            Receive("catcher", HomeCatcher, arrive);
            threw = true;
        }
        if ((dive || wallCatch) && !threw) Put(id, tCatch + 0.7, landing, pose: Poses.Stand);

        // A hit and run that ends on a line drive: the runner was already off the bag and is doubled off first.
        if (moves.FirstOrDefault(m => m.FromBase == 1 && m.ToBase == 1 && m.IsOut) is { } doubledOff)
            DoubleOffAtFirst(doubledOff, id, tCatch + ready);

        _s.Cue(tCatch, CueKind.Sound, "out");
        if (platePop) _s.Cue(tCatch, CueKind.Hold, "0.2");
        if (wallCatch)
        {
            _s.Cue(tCatch, CueKind.Sound, "cheer");
            _s.Cue(tCatch, CueKind.Banner, "CAUGHT AT THE WALL!");
            _s.Cue(tCatch, CueKind.Hold, "0.3");
        }
        if (dive)
        {
            _s.Cue(tCatch, CueKind.Sound, "cheer");
            _s.Cue(tCatch, CueKind.Banner, "DIVING CATCH!");
            if (pos is Position.Left or Position.Center or Position.Right)
                _s.Cue(tCatch, CueKind.Hold, "0.3");
        }
        _s.Cue(tCatch + (dive ? 0.9 : wallCatch ? 0.85 : 0.1), CueKind.Banner, _result.Description);
        CheerForRuns(moves, times);
    }

    // A fielder of average range (5 on the 1 to 9 scale) dives for one catch in ten, and the best for more than a third.
    private double DiveChance(Position pos)
    {
        int range = _before.Fielders.TryGetValue(pos, out var who) ? who.FieldingAt(pos).Range : 5;
        return Math.Clamp(0.10 + 0.07 * (range - 5), 0.04, 0.45);
    }

    private FieldPoint WallCatchPoint(Position pos)
    {
        double angle = Math.Clamp(_spots[pos].AngleDegrees + Between(-4, 4), -40, 40);
        return FieldPoint.FromPolar(angle, FieldGeometry.WallDistance(_before.Stadium, angle) - 5);
    }

    // Where a diving catch is made: well to one side of where he plays (a long way for an outfielder), and never in the stands.
    private FieldPoint DivePoint(Position pos, Catch kind)
    {
        var spot = _spots[pos];
        bool outfield = pos is Position.Left or Position.Center or Position.Right;
        double side = _rng.NextDouble() < 0.5 ? -1 : 1;
        double lateral = outfield ? Between(20, 32) : Between(9, 14);
        double forward = outfield ? Between(-18, 4) : Between(-3, 4);

        var p = new FieldPoint(spot.X + side * lateral, spot.Y + forward);
        if (!FieldGeometry.IsFair(p)) p = new FieldPoint(spot.X - side * lateral, spot.Y + forward);
        if (!FieldGeometry.IsFair(p)) return CatchPoint(pos, kind);
        if (outfield)
        {
            double limit = FieldGeometry.WallDistance(_before.Stadium, p.AngleDegrees) - 8;
            if (p.DistanceFromHome > limit) p = FieldPoint.FromPolar(p.AngleDegrees, limit);
        }
        return p;
    }

    // ── Error ────────────────────────────────────────────────────────────────

    // The fielder gets to the ball and misplays it: it gets away from him, he runs it down, and his throw is late.
    private void ErrorPlay()
    {
        var pos = _result.ErrorPosition ?? _result.Fielder ?? Position.Short;
        if (!Has(pos)) pos = FieldGeometry.FieldingPositions.FirstOrDefault(Has);
        if (!Has(pos))
        {
            Basic();
            return;
        }
        bool outfield = pos is Position.Left or Position.Center or Position.Right;

        ContactPitch();
        var how = ChooseMisplay(pos, outfield);
        if (how == Misplay.WildThrow)
        {
            WildThrow(pos);
            return;
        }

        var (tMiss, tPick) = how switch
        {
            Misplay.UnderGlove => BallUnderTheGlove(pos),
            Misplay.Dropped => DroppedFly(pos),
            Misplay.Misjudged => MisjudgedFly(pos),
            _ => BootedBall(pos, outfield),
        };
        if (outfield) _s.Cue(tMiss, CueKind.Hold, "0.2");
        AfterMisplay(pos, outfield, tMiss, tPick);
    }

    private enum Misplay { Booted, UnderGlove, WildThrow, Dropped, Misjudged }

    // Several ways to put it: a booted ball, a ball under the glove, a throw into the stands (an infielder only,
    // and only if there is a first baseman to throw it to), a fly dropped, a fly misjudged. Display only.
    private Misplay ChooseMisplay(Position pos, bool outfield)
    {
        double roll = _rng.NextDouble();
        if (outfield) return roll < 0.34 ? Misplay.Booted : roll < 0.67 ? Misplay.Dropped : Misplay.Misjudged;
        bool canThrowWild = pos is Position.Short or Position.Second or Position.Third && Has(Position.First);
        return roll < 0.34 ? Misplay.Booted : roll < 0.67 || !canThrowWild ? Misplay.UnderGlove : Misplay.WildThrow;
    }

    // He gets to it and boots it: it gets away from him a little, and he chases it down.
    private (double Miss, double Pick) BootedBall(Position pos, bool outfield)
    {
        string id = IdOf(pos);
        FieldPoint at;
        double tBall;
        if (outfield)
        {
            at = CatchPoint(pos, Catch.Fly);
            double d = at.DistanceFromHome;
            double go = Math.Max(Contact + 0.1, _s.TimeOf(id));
            tBall = Math.Max(Contact + 0.6 + d / 170, go + _s.PointOf(id).DistanceTo(at) / FielderSpeed);
            _s.Move("ball", tBall, at, 0.5, ease: Ease.Arc, apex: Math.Clamp(d * 0.4, 28, 80));
        }
        else
        {
            at = GroundLanding(pos);
            tBall = RollArrival(at);
            _s.Move("ball", tBall, at, 0.5, ease: Ease.Roll);
        }
        double tMiss = Fetch(pos, at, tBall, Poses.Crouch);
        Put("ball", tMiss, at, 0.5);
        _s.Cue(tMiss, CueKind.Effect, "error:booted", at);

        double side = _rng.NextDouble() < 0.5 ? -1 : 1;
        var away = new FieldPoint(at.X + side * Between(10, 17), at.Y + Between(8, 16));
        if (!FieldGeometry.IsFair(away)) away = at;
        double tRoll = tMiss + 0.5;
        Put("ball", tRoll, away, 0.5, ease: Ease.Roll);
        double tChase = tMiss + 0.3;
        double tPick = Math.Max(tRoll, tChase + at.DistanceTo(away) / FielderSpeed);
        Put(id, tMiss + 0.1, at, pose: Poses.Recover);
        Put(id, tChase, at, pose: Poses.Run);
        Put(id, tPick, away, pose: Poses.Crouch);
        Put("ball", tPick, away, 0.5);
        return (tMiss, tPick);
    }

    // It goes right through him, glove down at the grass, and on behind; he turns and runs it down.
    private (double Miss, double Pick) BallUnderTheGlove(Position pos)
    {
        string id = IdOf(pos);
        var at = GroundLanding(pos);
        var away = new FieldPoint(at.X + Between(-3, 3), at.Y + Between(16, 24));
        double tBall = RollArrival(at);
        _s.Move("ball", tBall, at, 0.5);            // at a steady pace as far as him, ...
        double tMiss = Fetch(pos, at, tBall, Poses.Crouch);
        _s.Cue(tMiss, CueKind.Effect, "error:underglove", at);
        double tRoll = tMiss + 0.55;
        Put("ball", tRoll, away, 0.5, ease: Ease.Roll);   // ... and on through
        double tChase = tMiss + 0.28;
        double tPick = Math.Max(tRoll, tChase + at.DistanceTo(away) / FielderSpeed);
        Put(id, tMiss + 0.12, at, pose: Poses.Turn);
        Put(id, tChase, at, pose: Poses.Run);
        Put(id, tPick, away, pose: Poses.Crouch);
        Put("ball", tPick, away, 0.5);
        return (tMiss, tPick);
    }

    // It hits the glove and pops out; he looks down, finds it at his feet and picks it up.
    private (double Miss, double Pick) DroppedFly(Position pos)
    {
        string id = IdOf(pos);
        var at = CatchPoint(pos, Catch.Fly);
        double d = at.DistanceFromHome;
        double go = Math.Max(Contact + 0.1, _s.TimeOf(id));
        double tBall = Math.Max(Contact + 0.6 + d / 170, go + _s.PointOf(id).DistanceTo(at) / FielderSpeed);
        _s.Move("ball", tBall, at, 4, ease: Ease.Arc, apex: Math.Clamp(d * 0.4, 28, 80));
        double tMiss = Fetch(pos, at, tBall);
        _s.Cue(tMiss, CueKind.Effect, "error:dropped", at);

        var away = new FieldPoint(at.X + Between(-2.5, 2.5), at.Y - Between(1.5, 3));
        Put("ball", tMiss, at, 4);
        Put("ball", tMiss + 0.3, away, 0.4, ease: Ease.Arc, apex: 3);
        Put(id, tMiss + 0.18, at, pose: Poses.Recover);
        double tPick = tMiss + 0.85;
        Put(id, tPick, away, pose: Poses.Crouch);
        Put("ball", tPick, away, 0.4);
        return (tMiss, tPick);
    }

    // He breaks in, hands up, and it lands behind him; he turns and chases it to the wall.
    private (double Miss, double Pick) MisjudgedFly(Position pos)
    {
        string id = IdOf(pos);
        var spot = _spots[pos];
        double angle = Math.Clamp(spot.AngleDegrees + Between(-6, 6), -43, 43);
        double limit = FieldGeometry.WallDistance(_before.Stadium, angle) - 8;
        var at = FieldPoint.FromPolar(angle, Math.Min(spot.DistanceFromHome + Between(14, 22), limit));
        var away = FieldPoint.FromPolar(angle, Math.Min(at.DistanceFromHome + Between(6, 12), limit));

        double go = Math.Max(Contact + 0.1, _s.TimeOf(id));
        var stepIn = spot.Towards(FieldGeometry.Home, 9 / spot.DistanceFromHome);
        Put(id, go, spot, pose: Poses.Run);
        Put(id, go + 0.3, stepIn, pose: Poses.Catch);
        Put(id, go + 0.44, stepIn, pose: Poses.Turn);

        double d = at.DistanceFromHome;
        double tBall = Math.Max(Contact + 0.6 + d / 170, go + 0.6);
        _s.Move("ball", tBall, at, 0, ease: Ease.Arc, apex: Math.Clamp(d * 0.4, 28, 80));
        _s.Cue(tBall, CueKind.Effect, "error:misjudged", at);
        double tRoll = tBall + 0.5;
        Put("ball", tRoll, away, 0.3, ease: Ease.Roll);
        double tPick = Fetch(pos, away, tRoll, Poses.Crouch);
        Put("ball", tPick, away, 0.5);
        return (tBall, tPick);
    }

    // The infielder fields it cleanly and throws it past the first baseman and into the stands.
    private void WildThrow(Position pos)
    {
        string id = IdOf(pos);
        var at = GroundLanding(pos);
        double tBall = RollArrival(at);
        _s.Move("ball", tBall, at, 0.5, ease: Ease.Roll);
        double tField = Fetch(pos, at, tBall, Poses.Tag);
        Put("ball", tField, at, 0.5);

        var moves = _result.RunnerMoves;
        var times = MoveAll(Contact + 0.1, SecondsPerBase, slide: SlidesHome);

        // Over his head and over the wall: 50 feet past the bag and 10 out is beyond the wall.
        var first = FieldGeometry.First;
        var wild = new FieldPoint(first.X + 50, first.Y + 10);
        double arrive = ThrowTo(id, wild, tField + Transfer);
        Put("ball", arrive + 0.4, wild, 0, ease: Ease.Roll);   // it drops among the fans

        string firstId = FielderId(Position.First);
        double pass = arrive - 0.35;   // the first baseman stretches as it goes over him
        Put(firstId, pass, first, 1.2, pose: Poses.Catch);
        Put(firstId, pass + 0.4, first, pose: Poses.Turn);

        _s.Cue(pass, CueKind.Effect, "error:wildthrow", first);
        _s.Cue(pass, CueKind.Sound, "error");
        _s.Cue(pass + 0.1, CueKind.Banner, _result.Description);
        CheerForRuns(moves, times);
    }

    // After the misplay: the runners go, the throw (when it comes) is late, and the error is announced.
    private void AfterMisplay(Position pos, bool outfield, double tMiss, double tPick)
    {
        string id = IdOf(pos);
        var moves = _result.RunnerMoves;
        var times = MoveAll(Contact + 0.1, SecondsPerBase, slide: SlidesHome);

        // The throw, when it comes, is late.
        var (receiver, point, arriveAfter) = ChooseThrowTarget(outfield ? Kind.Bloop : Kind.InfieldHit, pos, moves, times);
        if (receiver == id)
        {
            // He cannot throw to himself: the ball goes to first, or to the pitcher.
            if (id == "pitcher") (receiver, point, arriveAfter) = (FielderId(Position.First), FieldGeometry.First, null);
            else (receiver, point, arriveAfter) = ("pitcher", FieldGeometry.Mound, null);
        }
        double arrive = ThrowTo(id, point, tPick + Transfer, arriveAfter);
        Receive(receiver, point, arrive);

        _s.Cue(tMiss, CueKind.Sound, "error");
        _s.Cue(tMiss + 0.1, CueKind.Banner, _result.Description);
        CheerForRuns(moves, times);
    }
}
