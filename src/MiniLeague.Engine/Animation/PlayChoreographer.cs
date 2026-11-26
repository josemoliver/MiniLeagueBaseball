using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Animation;

/// <summary>
/// Turns a finished play into a script for showing it. The result is already decided, so this only makes up
/// plausible details (where the ball lands, who fields it, how the throw goes) that agree with the result.
/// It is pure: the same situation, result and seed always give the same script, and it never touches the game
/// or the simulation's random numbers.
/// </summary>
public static class PlayChoreographer
{
    public static PlayScript Build(PrePlaySnapshot before, PlayResult result, int seed) =>
        new Staging(before, result, new Random(seed)).Build();

    /// <summary>
    /// The field as it stands before a pitch (fielders in place, batter up, runners on their bases) and nothing
    /// else: a script of zero length, for showing the situation between plays.
    /// </summary>
    public static PlayScript BuildSetup(PrePlaySnapshot situation) =>
        new Staging(situation, new PlayResult(), new Random(0)).BuildSetup();
}

// The staging is split over a few files: this one has the cast, the pieces every play uses and the simple
// plays; Staging.Hits.cs, Staging.Outs.cs and Staging.Steals.cs have the rest.
internal sealed partial class Staging
{
    // Seconds at Normal speed.
    // These depend on the pitch (a fastball is quick, a curve and a change-up take longer), see PitchStyle.
    private double Release => _style.Release;           // pitcher lets go of the ball
    private double PlateArrival => _style.Arrival;      // ball reaches the plate
    private double Contact => _style.Arrival;           // ...which is where a hit starts
    private const double SecondsPerBase = 0.45;  // a runner covers 90 feet
    private const double TrotPerBase = 0.90;     // home run trot
    private const double StealSeconds = 1.2;     // a runner breaking for second
    private const double Settle = 0.5;           // time left after the last thing happens
    private const double FielderSpeed = 36;      // feet per second (a little faster than life, to keep plays short)
    private const double ThrowSpeedMax = 170;    // feet per second
    private const double ThrowSpeedMin = 55;
    private const double Transfer = 0.15;        // glove to hand before a throw
    private const double MaxStretch = 2.2;       // the most a runner is slowed so that a throw can beat him

    private readonly PrePlaySnapshot _before;
    private readonly PitchStyle _style;
    private readonly PlayResult _result;
    private readonly Random _rng;
    private readonly ScriptBuilder _s = new();
    private readonly string _offense;
    private readonly string _defense;
    private readonly IReadOnlyDictionary<Position, FieldPoint> _spots;
    private readonly double _batterX;

    public Staging(PrePlaySnapshot before, PlayResult result, Random rng)
    {
        _before = before;
        _result = result;
        _style = PitchStyle.Of(result.Pitch);
        _rng = rng;
        _offense = before.IsTop ? TeamSide.Visitor : TeamSide.Home;
        _defense = before.IsTop ? TeamSide.Home : TeamSide.Visitor;
        _spots = SpotsFor(before);
        _batterX = before.BatsFrom == BatHand.Left ? 3.5 : -3.5;  // a right-hander stands on the third-base side
    }

    /// <summary>Where the defense stands before a pitch: the alignment, with the outfield set for the batter.</summary>
    internal static IReadOnlyDictionary<Position, FieldPoint> SpotsFor(PrePlaySnapshot before) =>
        // The outfield plays a little deeper for a power hitter and shallower for a slap hitter.
        FieldGeometry.WithOutfieldDepth(
            FieldGeometry.FielderSpots(before.Alignment, before.BatsFrom, before.Stadium),
            FieldGeometry.OutfieldDepthFor(before.Batter.GetBattingStats(before.Pitcher.ThrowHand)),
            before.Stadium);

    public PlayScript Build()
    {
        SetCast();
        // A hit and run: the runner is off with the pitch, before anything else in the play happens. So is the runner on third on a
        // suicide squeeze; on a safety squeeze he takes his lead and waits to see the bunt.
        if (_result.HitAndRun) RunnerBreaks();
        else if (_result.Squeeze == SqueezeKind.Suicide) RunnerBreaks(3);
        else if (_result.Squeeze == SqueezeKind.Safety)
        {
            TakeLeads();
            _s.Cue(Release, CueKind.Banner, "SAFETY SQUEEZE");
        }
        switch (_result.Outcome)
        {
            case OutcomeType.Strikeout: Strikeout(); break;
            case OutcomeType.Walk or OutcomeType.HitByPitch or OutcomeType.IntentionalWalk: Walk(); break;
            case OutcomeType.HomeRun: HomeRun(); break;
            case OutcomeType.Single when _result.IsBunt: BuntSingle(); break;
            case OutcomeType.Single: Single(); break;
            case OutcomeType.SacrificeBunt: GroundOut(); break;       // the batter is thrown out at first, the runner moves up
            case OutcomeType.FieldersChoice: FieldersChoicePlay(); break;
            case OutcomeType.Double: ExtraBaseHit(triple: false); break;
            case OutcomeType.Triple: ExtraBaseHit(triple: true); break;
            case OutcomeType.GroundOut: GroundOut(); break;
            case OutcomeType.DoublePlay: DoublePlay(); break;
            case OutcomeType.FlyOut: CaughtBall(Catch.Fly); break;
            case OutcomeType.LineOut: CaughtBall(Catch.Line); break;
            case OutcomeType.PopOut: CaughtBall(Catch.Pop); break;
            case OutcomeType.Error: ErrorPlay(); break;
            case OutcomeType.StolenBase or OutcomeType.CaughtStealing: Steal(); break;
            default: Basic(); break;
        }
        ReturnToBases();
        return _s.Build(_result.Outcome.ToString(), Settle);
    }

    public PlayScript BuildSetup()
    {
        SetCast();
        return _s.Build("Setup", 0);
    }

    // ── Cast ─────────────────────────────────────────────────────────────────

    private static string FielderId(Position pos) => $"fielder-{pos}";

    // The pitcher and catcher have their own ids; everyone else is "fielder-<position>".
    private static string IdOf(Position pos) => pos switch
    {
        Position.C => "catcher",
        Position.P => "pitcher",
        _ => FielderId(pos),
    };

    private static string ActorId(RunnerMove m) => m.FromBase == 0 ? "batter" : $"runner-{m.Player.Id}";

    private void SetCast()
    {
        _s.AddActor(new Actor("pitcher", ActorKind.Pitcher, _before.Pitcher.Name, _defense, Hands.Of(_before.Pitcher.ThrowHand)), FieldGeometry.Mound, Poses.Stand);
        _s.AddActor(new Actor("catcher", ActorKind.Catcher, NameAt(Position.C), _defense,
            _before.Fielders.TryGetValue(Position.C, out var catcher) ? Hands.Of(catcher.ThrowHand) : Hands.Right),
            _spots[Position.C], Poses.Stand);
        foreach (var pos in FieldGeometry.FieldingPositions)
            if (_before.Fielders.ContainsKey(pos))
                _s.AddActor(new Actor(FielderId(pos), ActorKind.Fielder, NameAt(pos), _defense,
                    Hands.Of(_before.Fielders[pos].ThrowHand)), _spots[pos], Poses.Stand);

        _s.AddActor(new Actor("batter", ActorKind.Batter, _before.Batter.Name, _offense,
                Hands.Of(_before.BatsFrom), Stances.Of(_before.Batter, _before.Pitcher.ThrowHand)),
            new FieldPoint(_batterX, 0), Poses.Stand);
        foreach (var (runner, number) in new[] { (_before.First, 1), (_before.Second, 2), (_before.Third, 3) })
            if (runner is not null)
                _s.AddActor(new Actor($"runner-{runner.Player.Id}", ActorKind.Runner, runner.Player.Name, _offense),
                    FieldGeometry.BaseSpot(number), Poses.Stand);

        _s.AddActor(new Actor("ball", ActorKind.Ball, null, null), ReleasePoint, Poses.Stand, z: _style.HandHeight);
    }

    // Where the ball is when it is let go: in the pitching hand. The player draws it in that hand until it is thrown.
    private FieldPoint ReleasePoint =>
        new((_before.Pitcher.ThrowHand == ThrowHand.Left ? -1 : 1) * _style.HandSide, FieldGeometry.Mound.Y - 1);

    private string? NameAt(Position pos) => _before.Fielders.TryGetValue(pos, out var p) ? p.Name : null;

    // The pitcher and catcher are always there; the others only if the lineup has someone at the position.
    private bool Has(Position pos) => pos is Position.P or Position.C || _before.Fielders.ContainsKey(pos);

    private double Between(double low, double high) => low + _rng.NextDouble() * (high - low);

    private int PullSign => FieldGeometry.PullSign(_before.BatsFrom);

    // ── Leads ────────────────────────────────────────────────────────────────

    private IEnumerable<(BaseRunner Runner, int Number)> Runners =>
        new[] { (_before.First, 1), (_before.Second, 2), (_before.Third, 3) }.Where(r => r.Item1 is not null).Select(r => (r.Item1!, r.Item2));

    // A few feet off the base toward the next one: more for a fast runner (4 feet for the slowest, 10 for the fastest).
    internal static FieldPoint LeadFrom(int number, Player runner)
    {
        var bag = FieldGeometry.BaseSpot(number);
        var next = FieldGeometry.BaseSpot(number + 1);
        double feet = 4 + 1.5 * (Math.Clamp(runner.SpeedRating, 1, 5) - 1);
        return bag.Towards(next, feet / bag.DistanceTo(next));
    }

    private bool _leadsTaken;

    // As the pitcher winds up every runner steps off his base, so a runner who goes breaks from his lead.
    private void TakeLeads()
    {
        if (_leadsTaken) return;
        _leadsTaken = true;
        foreach (var (runner, number) in Runners)
        {
            string id = $"runner-{runner.Player.Id}";
            // A shuffle, not a run (nothing in the picture runs before the ball is hit).
            _s.Move(id, 0.03, FieldGeometry.BaseSpot(number), pose: Poses.Stand);
            _s.Move(id, Math.Max(0.1, _style.Release * 0.85), LeadFrom(number, runner.Player), pose: Poses.Stand);
        }
    }

    // A runner who has not gone anywhere gets back on his base when the play is over (the next picture has him on it).
    private void ReturnToBases()
    {
        double when = _s.LastTime - 0.2;
        foreach (var (runner, number) in Runners)
        {
            string id = $"runner-{runner.Player.Id}";
            // WHY: a runner who broke on a hit and run and then had to come back (a fly ball, a pop-up) is not at his lead,
            // but he has not gone anywhere either: his move says he stayed put, so he returns to his base like the rest.
            var move = _result.RunnerMoves.FirstOrDefault(m => m.FromBase == number);
            bool stayedPut = move is null ? _s.PointOf(id).DistanceTo(LeadFrom(number, runner.Player)) <= 0.01
                                          : move.Fate == RunnerFate.StayedPut;
            if (!stayedPut) continue;
            double at = Math.Max(when, _s.TimeOf(id));
            _s.Move(id, at, _s.PointOf(id), pose: Poses.Stand);
            _s.Move(id, at + 0.3, FieldGeometry.BaseSpot(number), pose: Poses.Stand);
        }
    }

    // ── Pieces every play uses ───────────────────────────────────────────────

    // Adds a key, never earlier than the actor's last one (so pieces can be put together without worrying).
    private void Put(string id, double t, FieldPoint at, double z = 0, string? pose = null,
                     Ease ease = Ease.Linear, double apex = 0, string? facing = null) =>
        _s.Move(id, Math.Max(t, _s.TimeOf(id)), at, z, pose, ease, apex, facing);

    /// <summary>
    /// The wind-up and the pitch, ending with the ball at <paramref name="end"/> at the plate time. How the pitcher
    /// winds up and how the ball travels depend on the pitch (see <see cref="PitchStyle"/>).
    /// </summary>
    private void Pitch(FieldPoint end, double endZ)
    {
        var style = _style;
        TakeLeads();
        const string mound = "pitcher";
        // WHY: with a runner on, a pitcher pitches from the stretch (hands set together, no arms-over-the-head wind-up),
        // as real pitchers do so that the runner cannot get a big jump. The timings are the same, only the pose differs.
        bool runnersOn = _before.First is not null || _before.Second is not null || _before.Third is not null;
        if (style.Windup is double windup) _s.Move(mound, windup, FieldGeometry.Mound, pose: runnersOn ? Poses.Stretch : Poses.Windup);
        _s.Move(mound, style.Kick, FieldGeometry.Mound, pose: Poses.Kick);
        _s.Move(mound, style.Release, FieldGeometry.Mound, pose: style.ReleasePose);
        _s.Move(mound, style.Release + 0.3, FieldGeometry.Mound, pose: Poses.Stand);

        // A pitchout: the catcher has been told, and steps out of the box to take it.
        if (_result.Pitch == PitchType.Pitchout)
            _s.Move("catcher", 0.1, new FieldPoint(-Math.Sign(_batterX) * 3, FieldGeometry.CatcherSpot.Y), pose: Poses.Catch);

        // NOTE: the pitch's name is a cue for the picture to label the ball in flight; it never feeds back into the game.
        _s.Cue(style.Release, CueKind.Effect, $"pitch:{_result.Pitch}");

        var start = ReleasePoint;
        double startZ = style.HandHeight;
        _s.Move("ball", style.Release, start, startZ);
        if (style.Break != 0)
        {
            // It leaves the hand straight and breaks late, toward the side the pitcher's glove is on.
            double side = _before.Pitcher.ThrowHand == ThrowHand.Left ? 1 : -1;
            double at = style.Release + (style.Arrival - style.Release) * 0.6;
            var mid = start.Towards(end, 0.6);
            _s.Move("ball", at, new FieldPoint(mid.X - side * style.Break * 0.15, mid.Y), startZ + (endZ - startZ) * 0.6);
        }
        _s.Move("ball", style.Arrival, end, endZ, ease: style.Apex > 0 ? Ease.Arc : Ease.Linear, apex: style.Apex);
    }

    // A pitch that is hit: the ball is at the plate at contact, and the batter swings.
    private void ContactPitch()
    {
        Pitch(FieldGeometry.Home, 3);
        if (_result.IsBunt) SquareToBunt();
        else Swing(Contact);
        _s.Cue(Contact, CueKind.Effect, "contact", FieldGeometry.Home);
    }

    // WHY: a bunter squares around as the pitcher delivers, so the bat is already level over the plate when the ball
    // arrives; the ball is then deadened, not driven (the fielders' reaction and the roll are in Staging.Bunts.cs).
    private void SquareToBunt()
    {
        var at = new FieldPoint(_batterX, 0);
        double square = Math.Max(0.1, _style.Release);
        _s.Move("batter", Math.Max(0.02, square - 0.08), at, pose: Poses.Stand);
        _s.Move("batter", square, at, pose: Poses.Bunt);
        _s.Cue(square, CueKind.Banner, "BUNT!");
    }

    private static double SpeedFactor(Player p) => 1 - 0.075 * (p.SpeedRating - 3);

    /// <summary>
    /// Runs a runner (or the batter) through his move. Returns the time he reaches each base on the way
    /// (4 is home). A runner who does not move, or a batter who is simply out, returns nothing.
    /// If <paramref name="minArrival"/> is given he is slowed (and, if need be, held back) so that he does not
    /// get there sooner: this is how a throw that has to beat him does. A runner who slides ends the play in a slide.
    /// </summary>
    private Dictionary<int, double> MoveRunner(RunnerMove m, double start, double secondsPerBase,
                                               double? minArrival = null, bool slide = false)
    {
        var times = new Dictionary<int, double>();
        string id = ActorId(m);
        if (m.Fate == RunnerFate.StayedPut) return times;
        if (m.Fate == RunnerFate.Retired)
        {
            WalkOff(id, start);
            return times;
        }

        int target = m.Fate == RunnerFate.Scored ? 4 : m.ToBase;
        int legs = target - m.FromBase;
        if (legs <= 0) return times;

        double perLeg = secondsPerBase * SpeedFactor(m.Player);
        double outOfTheBox = m.FromBase == 0 ? 0.15 : 0;   // the batter has to get out of the box
        double total = legs * perLeg + outOfTheBox;
        start = Math.Max(start, _s.TimeOf(id));
        if (minArrival is double need && start + total < need)
        {
            double factor = Math.Min(MaxStretch, (need - start) / total);
            perLeg *= factor;
            outOfTheBox *= factor;
            total *= factor;
            if (start + total < need) start = need - total;   // hold at the base until the right moment
        }

        _s.Move(id, start, _s.PointOf(id), pose: Poses.Run);
        double t = start;
        for (int b = m.FromBase + 1; b <= target; b++)
        {
            double leg = perLeg + (b == m.FromBase + 1 ? outOfTheBox : 0);
            if (b == target && slide && leg > 0.35)
                _s.Move(id, t + leg - 0.3, _s.PointOf(id).Towards(FieldGeometry.BaseSpot(b), 0.7), pose: Poses.Slide);
            t += leg;
            _s.Move(id, t, FieldGeometry.BaseSpot(b), pose: Poses.Run);
            times[b] = t;
        }
        _s.EndPose(id, m.IsOut ? Poses.Out : slide ? Poses.Safe : Poses.Stand);
        if (m.IsOut || slide)
        {
            var call = FieldGeometry.BaseSpot(target);
            _s.Cue(Math.Max(0, t - 0.25), CueKind.Effect, "focus", call);
            _s.Cue(t, CueKind.Effect, "call", call);
            _s.Cue(t, CueKind.Hold, "0.22");
        }
        return times;
    }

    private void WalkOff(string id, double start)
    {
        _s.Move(id, Math.Max(start, _s.TimeOf(id)), _s.PointOf(id), pose: Poses.Run);
        _s.Move(id, Math.Max(start, _s.TimeOf(id)) + 0.8, new FieldPoint(-40, -14), pose: Poses.Run);
        _s.EndPose(id, Poses.Stand);
    }

    /// <summary>
    /// Runs every runner through his move from <paramref name="start"/>, except those <paramref name="skip"/> says
    /// to leave alone. The result lines up with the result's moves (empty for a runner who did not run).
    /// </summary>
    private Dictionary<int, double>[] MoveAll(double start, double secondsPerBase,
        Func<RunnerMove, bool>? slide = null, Func<RunnerMove, bool>? skip = null) =>
        _result.RunnerMoves
            .Select(m => skip?.Invoke(m) == true
                ? new Dictionary<int, double>()
                : MoveRunner(m, start, secondsPerBase, slide: slide?.Invoke(m) ?? false))
            .ToArray();

    private void Swing(double at)
    {
        _s.Move("batter", at - 0.1, new FieldPoint(_batterX, 0), pose: Poses.Stand);
        _s.Move("batter", at, new FieldPoint(_batterX, 0), pose: Poses.Swing);
    }

    private void CatcherHolds(FieldPoint at, double time) =>
        _s.Move("catcher", time, at, pose: Poses.Catch);

    /// <summary>
    /// Picks when a throw is released and when it arrives. It is thrown as fast as it can be, but if it
    /// must not get there before <paramref name="arriveAfter"/> (the runner is safe), it is thrown slower
    /// and, if that is not enough, held a moment first.
    /// </summary>
    private static (double Release, double Arrive) SolveThrow(double earliest, double distance, double? arriveAfter)
    {
        double release = earliest;
        double arrive = release + distance / ThrowSpeedMax;
        if (arriveAfter is double after && arrive < after)
        {
            double duration = Math.Min(after - release, distance / ThrowSpeedMin);
            arrive = release + duration;
            if (arrive < after)
            {
                release += after - arrive;
                arrive = after;
            }
        }
        return (release, arrive);
    }

    /// <summary>
    /// A fielder gets to a point (he starts when the ball is hit), arriving when the ball does or when he can,
    /// whichever is later. Returns when he gets there.
    /// </summary>
    private double Fetch(Position pos, FieldPoint to, double ballTime, string pose = Poses.Catch,
                         string? facing = null)
    {
        string id = IdOf(pos);
        var from = _s.PointOf(id);
        double go = Math.Max(Contact + 0.1, _s.TimeOf(id));
        double arrive = Math.Max(ballTime, go + from.DistanceTo(to) / FielderSpeed);
        _s.Move(id, go, from, pose: Poses.Run);
        // WHY: under a ball that is still in the air a fielder has his glove up before it arrives, not only at the catch.
        double early = arrive - 0.3;
        if ((pose is Poses.Catch or Poses.OverShoulder) && early > go + 0.2)
            _s.Move(id, early, from.Towards(to, (early - go) / (arrive - go)),
                pose: pose == Poses.OverShoulder ? Poses.Track : Poses.Catch, facing: facing);
        if (pose == Poses.Dive && arrive - go > 0.35 && from.DistanceTo(to) > 8)
        {
            double launch = arrive - 0.3;
            double airborne = arrive - 0.1;
            _s.Move(id, launch, from.Towards(to, (launch - go) / (arrive - go)), pose: Poses.Dive);
            _s.Move(id, airborne, from.Towards(to, (airborne - go) / (arrive - go)), z: 1.6);
        }
        _s.Move(id, arrive, to, pose: pose, facing: facing);
        return arrive;
    }

    // WHY: The nearest other outfielder closes some of the gap, but leaves the catch or pickup to the chosen fielder.
    private void SupportOutfielder(Position primary, FieldPoint ballAt, double fieldedAt)
    {
        var backup = FieldGeometry.Outfielders.Where(p => p != primary && Has(p))
            .OrderBy(p => _spots[p].DistanceTo(ballAt)).Select(p => (Position?)p).FirstOrDefault();
        if (backup is not { } other) return;

        string id = IdOf(other);
        var from = _s.PointOf(id);
        double distance = from.DistanceTo(ballAt);
        double start = Math.Max(Contact + 0.12, _s.TimeOf(id));
        double travel = Math.Min(Math.Min(44, Math.Max(0, distance - 28)),
            Math.Max(0, fieldedAt + 0.2 - start) * FielderSpeed);
        if (travel < 5) return;

        _s.Move(id, start, from, pose: Poses.Run);
        _s.Move(id, start + travel / FielderSpeed, from.Towards(ballAt, travel / distance), pose: Poses.Stand);
    }

    // WHY: On a throw to first or home, the pitcher heads behind the bag in case the receiver misses it.
    private void BackUpThrow(string thrower, string receiver, FieldPoint bag, double ballArrives)
    {
        if (thrower == "pitcher" || receiver == "pitcher") return;
        FieldPoint? behind = bag.DistanceTo(FieldGeometry.First) < 4
            ? new(FieldGeometry.First.X + 12, FieldGeometry.First.Y + 4)
            : bag.DistanceTo(FieldGeometry.Home) < 4 ? new(12, -7) : null;
        if (behind is not { } target) return;

        var from = _s.PointOf("pitcher");
        double start = Math.Max(Contact + 0.12, _s.TimeOf("pitcher"));
        double distance = from.DistanceTo(target);
        double travel = Math.Min(distance, Math.Max(0, ballArrives + 0.2 - start) * FielderSpeed);
        if (travel < 6) return;

        _s.Move("pitcher", start, from, pose: Poses.Run);
        _s.Move("pitcher", start + travel / FielderSpeed, from.Towards(target, travel / distance), pose: Poses.Stand);
    }

    /// <summary>
    /// The holder throws the ball to a point. Returns when it arrives (see <see cref="SolveThrow"/> for
    /// <paramref name="arriveAfter"/>).
    /// </summary>
    private double ThrowTo(string throwerId, FieldPoint to, double earliestRelease,
                           double? arriveAfter = null, bool skipTransfer = false)
    {
        var from = _s.PointOf(throwerId);
        var (release, arrive) = SolveThrow(earliestRelease, from.DistanceTo(to), arriveAfter);
        // WHY: a man who has just fielded the ball moves it from the glove to his throwing hand before he throws (the
        // transfer). It only shows when there is time for it between his last move and the release.
        double transfer = release - 0.12;
        if (!skipTransfer && transfer > _s.TimeOf(throwerId) + 0.02)
            Put(throwerId, transfer, from, pose: Poses.Transfer);
        Put("ball", release, from, 3);
        Put(throwerId, release, from, pose: Poses.Throw);
        Put("ball", arrive, to, 4, ease: Ease.Arc, apex: 3 + from.DistanceTo(to) * 0.05);
        Put(throwerId, release + 0.3, from, pose: Poses.Stand);
        return arrive;
    }

    // WHY: A relay has a visible catch, turn and planted throw instead of changing directly from catch to release.
    private double SetForRelay(string id, FieldPoint target, double receivedAt)
    {
        var at = _s.PointOf(id);
        double dx = target.X - at.X, dy = target.Y - at.Y;
        string facing = Math.Abs(dx) > Math.Abs(dy) * 0.8
            ? (dx < 0 ? "left" : "right") : (dy < 0 ? "front" : "back");
        _s.Move(id, receivedAt + 0.1, at, pose: Poses.Turn, facing: facing);
        _s.Move(id, receivedAt + 0.25, at, pose: Poses.Plant, facing: facing);
        _s.Cue(receivedAt + 0.25, CueKind.Hold, "0.18");
        return receivedAt + 0.42;
    }

    /// <summary>The receiver gets to a point to take a throw arriving at <paramref name="arrive"/>.</summary>
    private void Receive(string id, FieldPoint at, double arrive, string pose = Poses.Catch,
                         string? facing = null, bool tagAtHome = true)
    {
        var from = _s.PointOf(id);
        double travel = from.DistanceTo(at) / FielderSpeed;
        if (travel > 0.01)
            Put(id, Math.Max(_s.TimeOf(id), arrive - travel - (pose == Poses.Reach ? 0.25 : 0.1)), from, pose: Poses.Run);
        // WHY: The first baseman has the glove out before the ball reaches him, with a foot already on the bag.
        if (pose == Poses.Reach && arrive - 0.18 > _s.TimeOf(id) + travel)
            _s.Move(id, arrive - 0.18, at, pose: Poses.Reach, facing: "left");
        if (id == "catcher" && at.DistanceTo(FieldGeometry.Home) < 4 && arrive - 0.16 > _s.TimeOf(id) + 0.02)
            _s.Move(id, arrive - 0.16, at, pose: Poses.Plant, facing: facing);
        _s.Move(id, arrive, at, pose: pose, facing: pose == Poses.Reach ? "left" : facing);

        // At the plate the catcher sweeps a tag across, a beat after the ball is in his glove.
        if (tagAtHome && id == "catcher" && at.DistanceTo(FieldGeometry.Home) < 4)
        {
            Put(id, arrive + 0.12, FieldGeometry.Home, pose: Poses.Tag);
            Put(id, arrive + 0.6, FieldGeometry.Home, pose: Poses.Stand);
        }
    }

    private Position? Nearest(IEnumerable<Position> positions, FieldPoint to)
    {
        Position? best = null;
        double bestDistance = double.MaxValue;
        foreach (var pos in positions.Where(Has))
        {
            double d = _spots[pos].DistanceTo(to);
            if (d < bestDistance) { best = pos; bestDistance = d; }
        }
        return best;
    }

    // Fielders on the pull side get more of the balls.
    private Position PickByPull(IReadOnlyList<Position> candidates)
    {
        var weights = candidates.Select(p => Math.Sign(_spots[p].X) == PullSign ? 1.5 : 1.0).ToList();
        double pick = _rng.NextDouble() * weights.Sum();
        int i = 0;
        while (i < candidates.Count - 1 && (pick -= weights[i]) >= 0) i++;
        return candidates[i];
    }

    // The latest time any of the picked runners reaches a base, plus a moment (null if none do).
    private static double? LatestAt(IReadOnlyList<RunnerMove> moves, Dictionary<int, double>[] times, int bas,
                                    Func<RunnerMove, bool> pick)
    {
        double? latest = null;
        for (int i = 0; i < moves.Count; i++)
            if (pick(moves[i]) && times[i].TryGetValue(bas, out var t)) latest = Math.Max(latest ?? 0, t);
        return latest + 0.2;
    }

    // ── Strikeout ────────────────────────────────────────────────────────────

    private void Strikeout()
    {
        var glove = new FieldPoint(0, -4);
        Pitch(glove, 2.5);
        // A hit and run: the batter swings and misses (he is told to protect the runner), and the runner is going.
        if (_result.HitAndRun || _rng.NextDouble() < 0.7) Swing(PlateArrival - 0.05);
        CatcherHolds(_spots[Position.C], PlateArrival);

        // Strike 'em out, throw 'em out (or the steal works): the runner is going to second and the catcher throws.
        var goer = _result.HitAndRun ? _result.RunnerMoves.FirstOrDefault(m => m.FromBase == 1 && m.ToBase == 2) : null;
        if (goer is not null)
        {
            Put("pitcher", PlateArrival + 0.05, FieldGeometry.Mound, pose: Poses.Cheer);
            Put("pitcher", PlateArrival + 0.7, FieldGeometry.Mound, pose: Poses.Stand);
            Put("batter", PlateArrival + 0.1, new FieldPoint(_batterX, 0), pose: Poses.Slump);
            _s.Cue(PlateArrival, CueKind.Sound, "strikeout");
            MoveAll(PlateArrival + 0.7, SecondsPerBase, skip: m => m == goer);
            StealThrow(goer, caught: goer.IsOut);
            return;
        }

        // The pitcher pumps his fist; the batter's head drops before he walks off.
        Put("pitcher", PlateArrival + 0.05, FieldGeometry.Mound, pose: Poses.Cheer);
        Put("pitcher", PlateArrival + 0.7, FieldGeometry.Mound, pose: Poses.Stand);
        Put("batter", PlateArrival + 0.1, new FieldPoint(_batterX, 0), pose: Poses.Slump);

        _s.Cue(PlateArrival, CueKind.Sound, "strikeout");
        _s.Cue(PlateArrival + 0.1, CueKind.Banner, _result.Description);
        MoveAll(PlateArrival + 0.7, SecondsPerBase);
    }

    // ── Walk, hit by pitch, intentional walk ─────────────────────────────────

    private void Walk()
    {
        double outside = -Math.Sign(_batterX) * 3.2;   // away from the batter
        double jogStart;

        switch (_result.Outcome)
        {
            case OutcomeType.IntentionalWalk:
            {
                // The catcher stands and the ball is lobbed wide.
                var wide = new FieldPoint(outside * 1.6, -5);
                _s.Move("catcher", 0.1, wide, pose: Poses.Catch);
                Pitch(wide, 3.5);
                jogStart = PlateArrival + 0.3;
                break;
            }
            case OutcomeType.HitByPitch:
            {
                var batter = new FieldPoint(_batterX, 0);
                Pitch(batter, 2.5);
                _s.Move("batter", PlateArrival, batter, pose: Poses.Hit);
                jogStart = PlateArrival + 0.5;
                break;
            }
            default:
            {
                var wide = new FieldPoint(outside, -4);
                Pitch(wide, 2.5);
                CatcherHolds(new FieldPoint(outside, -5), PlateArrival);
                jogStart = PlateArrival + 0.2;
                break;
            }
        }

        _s.Cue(PlateArrival, CueKind.Sound, "walk");
        _s.Cue(PlateArrival + 0.1, CueKind.Banner, _result.Description);
        MoveAll(jogStart, SecondsPerBase);
    }

    // Mostly the pull side, some the other way, a few up the middle.
    private double RollAngle()
    {
        double roll = _rng.NextDouble();
        double angle = roll < 0.55 ? PullSign * Between(8, 38)
                     : roll < 0.80 ? -PullSign * Between(5, 38)
                     : Between(-12, 12);
        return Math.Clamp(angle, -40, 40);
    }

    // ── Home run ─────────────────────────────────────────────────────────────

    private void HomeRun()
    {
        var park = _before.Stadium;
        ContactPitch();

        // Over the wall, somewhere in the seats.
        double angle = RollAngle();
        double wall = FieldGeometry.WallDistance(park, angle);
        double distance = wall + Between(8, 45);
        var landing = FieldPoint.FromPolar(angle, distance);
        double flight = 2.5 + distance / 450;
        double land = Contact + flight;
        double clear = Contact + flight * 0.86;
        var wallTop = FieldPoint.FromPolar(angle, wall);
        double wallHeight = FieldGeometry.WallHeight(park, angle);
        // WHY: Separate keys show the ball climbing, clearing the fence, then dropping into the seats.
        _s.Move("ball", Contact + flight * 0.52, FieldPoint.FromPolar(angle, distance * 0.55), 60 + distance * 0.06);
        _s.Move("ball", clear, wallTop, wallHeight + 14);
        _s.Move("ball", land, landing, wallHeight + 2);

        // The nearest outfielder runs only as far as he can before the ball clears the wall.
        var wallSpot = FieldPoint.FromPolar(angle, wall - 4);
        var fielder = Nearest(FieldGeometry.Outfielders, wallSpot);
        if (fielder is { } pos)
        {
            var from = _spots[pos];
            double start = Contact + 0.12;
            double distanceToWall = from.DistanceTo(wallSpot);
            double travel = Math.Min(distanceToWall, Math.Max(0, clear - start - 0.12) * FielderSpeed);
            var at = from.Towards(wallSpot, distanceToWall > 0 ? travel / distanceToWall : 1);
            _s.Move(FielderId(pos), start, from, pose: Poses.Run);
            _s.Move(FielderId(pos), start + travel / FielderSpeed, at, pose: Poses.Track, facing: "back");
            _s.Move(FielderId(pos), clear + 0.18, at, pose: Poses.Stand, facing: "back");
        }

        // The pitcher follows the flight and drops his head only when it clears the fence.
        Put("pitcher", Contact + 0.35, FieldGeometry.Mound, pose: Poses.Track);
        Put("pitcher", clear + 0.18, FieldGeometry.Mound, pose: Poses.Slump);

        Put("batter", Contact + 0.12, new FieldPoint(_batterX, 0), pose: Poses.Watch);
        var moves = _result.RunnerMoves;
        var times = MoveAll(Contact + 0.25, TrotPerBase, skip: m => m.FromBase == 0);
        for (int i = 0; i < moves.Count; i++)
            if (moves[i].FromBase == 0)
                times[i] = MoveRunner(moves[i], Contact + 0.68, TrotPerBase);

        GreetAtPlate(times, clear);

        _s.Cue(Contact, CueKind.Sound, "homerun");
        _s.Cue(clear, CueKind.Effect, "wallclear", wallTop);
        _s.Cue(clear, CueKind.Effect, "stand");   // the crowd rises when the ball is gone
        _s.Cue(clear, CueKind.Banner, "IT'S GONE!");
        _s.Cue(clear, CueKind.Hold, "0.28");
        _s.Cue(land, CueKind.Effect, "burst", landing);
        _s.Cue(Contact + 0.25, CueKind.Banner, "HIGH AND DEEP...");
        _s.Cue(land + 0.08, CueKind.Banner, _result.Description);
        _s.Cue(land + 0.08, CueKind.Hold, "0.35");
    }

    // Everyone who scored on a home run is met at the plate: teammates come out of the dugout for a high five.
    private void GreetAtPlate(Dictionary<int, double>[] times, double clear)
    {
        var moves = _result.RunnerMoves;
        double? arrival = null;
        for (int i = 0; i < moves.Count; i++)
            if (moves[i].Fate == RunnerFate.Scored && times[i].TryGetValue(4, out var t))
            {
                _s.EndPose(ActorId(moves[i]), Poses.Cheer);
                arrival = Math.Max(arrival ?? 0, t);
            }
        if (arrival is not double home) return;

        // The visitors' dugout is on the first-base side and the home team's on the third-base side.
        double side = _offense == TeamSide.Home ? -1 : 1;
        for (int k = 0; k < 3; k++)
        {
            string id = $"mate-{k}";
            _s.AddActor(new Actor(id, ActorKind.Teammate, null, _offense), new FieldPoint(side * 150, 30 + 3 * k), Poses.Stand);
            var spot = new FieldPoint(side * (8 + 5 * k), -3 - 2.5 * (k % 2));
            double greet = home + 0.25;
            double start = Math.Max(clear + 0.08, greet - 2.2);
            _s.Move(id, start, new FieldPoint(side * 150, 30 + 3 * k), pose: Poses.Run);
            _s.Move(id, greet, spot, pose: Poses.Cheer);
            _s.Move(id, greet + 0.5, spot, z: 1.6, pose: Poses.Cheer);
            _s.Move(id, greet + 0.9, spot, pose: Poses.Cheer);
        }
    }

    // ── Everything else (an outcome without a template of its own) ───────────

    // The pitch, the runners' moves, and the result line. No ball in play is shown.
    private void Basic()
    {
        Pitch(new FieldPoint(0, -4), 2.5);
        CatcherHolds(_spots[Position.C], PlateArrival);
        _s.Cue(PlateArrival + 0.1, CueKind.Banner, _result.Description);
        MoveAll(PlateArrival + 0.3, SecondsPerBase);
    }
}
