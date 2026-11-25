using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Animation;

// A runner breaking for second or third (or both runners, on a double steal), and the catcher's throw.
internal sealed partial class Staging
{
    private void Steal()
    {
        // WHY: any runner can go. The throw is to the base of the runner the play is about: the one who was caught, else the
        // lead runner. On a double steal the other runner simply runs, to the base he reached (or the one he was thrown out at).
        var goers = _result.RunnerMoves
            .Where(m => m.FromBase > 0 && (m.IsOut || m.ToBase > m.FromBase))
            .OrderByDescending(m => m.FromBase).ToList();
        var caughtMove = goers.FirstOrDefault(m => m.IsOut);
        var runner = caughtMove ?? goers.FirstOrDefault();
        if (runner is null)
        {
            Basic();
            return;
        }
        bool caught = caughtMove is not null;

        // The pitch goes to the catcher, and the runner breaks as the pitcher delivers.
        Pitch(new FieldPoint(0, -4), 2.5);
        CatcherHolds(_spots[Position.C], PlateArrival);
        MoveAll(Release, SecondsPerBase, skip: m => m == runner);
        StealThrow(runner, caught);
    }

    // The catcher pops up and throws to the covering infielder, and the runner slides in. It is the second half of a steal,
    // and of a hit and run on which the batter struck out (the runner was going on the pitch).
    private void StealThrow(RunnerMove runner, bool caught)
    {
        var bag = FieldGeometry.BaseSpot(runner.ToBase);
        // At second the shortstop or the second baseman covers (it varies); at third, the third baseman.
        var coverPos = runner.ToBase == 3
            ? Position.Third
            : _rng.NextDouble() < 0.5 ? Position.Short : Position.Second;
        if (!Has(coverPos))
            coverPos = coverPos == Position.Short ? Position.Second : Position.Short;
        string cover = Has(coverPos) ? IdOf(coverPos) : "pitcher";
        var gloveSide = new FieldPoint(bag.X + (coverPos == Position.Short ? -4 : coverPos == Position.Third ? 3 : 4), bag.Y - 1);

        // WHY: The catcher gets upright and sets his feet; the covering infielder reaches second before the throw.
        var coverFrom = _s.PointOf(cover);
        double coverGo = Math.Max(Release + 0.1, _s.TimeOf(cover));
        double ready = Math.Max(coverGo + coverFrom.DistanceTo(gloveSide) / FielderSpeed, PlateArrival + 0.12);
        _s.Move(cover, coverGo, coverFrom, pose: Poses.Run);
        _s.Move(cover, ready, gloveSide, pose: Poses.Plant);
        Put("catcher", PlateArrival + 0.1, _spots[Position.C], pose: Poses.Pop);
        Put("catcher", PlateArrival + 0.25, _spots[Position.C], pose: Poses.Plant);
        double release = PlateArrival + 0.42;

        double arrive;
        double end;
        if (caught)
        {
            // The throw is there first; the runner is slowed so that it is.
            arrive = ThrowTo("catcher", gloveSide, release, arriveAfter: ready + 0.08);
            var times = MoveRunner(runner, Release, StealSeconds, minArrival: arrive + 0.12, slide: true);
            Receive(cover, gloveSide, arrive);
            end = times[runner.ToBase];
            Put(cover, end, gloveSide, pose: Poses.Tag);
            _s.Cue(end, CueKind.Sound, "caught");
        }
        else
        {
            // The runner is there first; the throw is slowed (or held) so that it is.
            var times = MoveRunner(runner, Release, StealSeconds, slide: true);
            double ran = times[runner.ToBase];
            arrive = ThrowTo("catcher", gloveSide, release, arriveAfter: Math.Max(ran + 0.15, ready + 0.08));
            Receive(cover, gloveSide, arrive);
            Put(cover, arrive + 0.08, gloveSide, pose: Poses.Tag);
            end = arrive + 0.08;
            _s.Cue(ran, CueKind.Sound, "steal");
        }
        _s.Cue(end + 0.1, CueKind.Banner, _result.Description);
    }
}
