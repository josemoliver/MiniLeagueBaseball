using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Animation;

/// <summary>
/// The scenes around the game rather than the plays in it: the start of the game, a new half-inning, a new
/// pitcher or pinch hitter walking out, a visit to the mound, a walk-off celebration and the final score.
/// Like <see cref="PlayChoreographer"/> they are display only. They are built from facts the game already
/// has (two snapshots of the field, or the game itself) and never change anything.
/// </summary>
public static class SceneChoreographer
{
    /// <summary>
    /// What the field must show now that it did not before, as scenes to play one after the other.
    /// A new half-inning is one scene (everyone changes places); within a half-inning a new pitcher is one scene
    /// and new hitters, runners and fielders are another. Nothing has changed: an empty list.
    /// </summary>
    public static IReadOnlyList<PlayScript> Between(PrePlaySnapshot from, PrePlaySnapshot to)
    {
        if (from.Inning != to.Inning || from.IsTop != to.IsTop)
            return [new Scenes(to).HalfInning(from)];

        var scenes = new List<PlayScript>();
        if (from.Pitcher.Id != to.Pitcher.Id) scenes.Add(new Scenes(to).PitchingChange(from));
        var swaps = new Scenes(to).Substitutions(from);
        if (swaps is not null) scenes.Add(swaps);
        return scenes;
    }

    /// <summary>Fielders move into the alignment chosen before the pitch; this does not change the game state.</summary>
    public static PlayScript AlignmentChange(PrePlaySnapshot before, DefensiveAlignment alignment) =>
        new Scenes(before with { Alignment = alignment }).AlignmentChange(before);

    /// <summary>
    /// What the field must show once a play is over: a new half-inning, or a new pitcher the computer brought in.
    /// (The next batter and the runners have changed too, of course, so only those two are looked for.)
    /// </summary>
    public static IReadOnlyList<PlayScript> AfterPlay(PrePlaySnapshot lastPlay, PrePlaySnapshot now)
    {
        if (lastPlay.Inning != now.Inning || lastPlay.IsTop != now.IsTop)
            return [new Scenes(now).HalfInning(lastPlay)];
        if (lastPlay.Pitcher.Id != now.Pitcher.Id)
            return [new Scenes(now).PitchingChange(lastPlay)];
        return [];
    }

    /// <summary>A manager comes out and argues at the plate, and is thrown out.</summary>
    public static PlayScript Ejection(PrePlaySnapshot lastPlay, GameEvent thrownOut) => new Scenes(lastPlay).Ejection(thrownOut);

    /// <summary>
    /// A player is hurt: trainers run out, he is helped off and (if the game has already named him) his replacement comes on.
    /// <paramref name="lastPlay"/> is the situation before the play that hurt him; <paramref name="now"/> the one after it.
    /// </summary>
    public static PlayScript Injury(PrePlaySnapshot lastPlay, GameEvent hurt, PrePlaySnapshot? now) =>
        new Scenes(lastPlay).Injury(hurt, ReplacementFor(hurt, now));

    /// <summary>A rain delay: the tarp comes on, everyone goes in, and they come out again when it stops.</summary>
    public static PlayScript RainDelay(PrePlaySnapshot situation, GameEvent delay) => new Scenes(situation).RainDelay(delay);

    // Who has taken the place of a hurt fielder or pitcher, if the field after the play shows his team in the field.
    private static string? ReplacementFor(GameEvent hurt, PrePlaySnapshot? now)
    {
        if (now is null) return null;
        bool fielding = now.IsTop != hurt.Visitor;   // the top of an inning has the home team in the field
        if (!fielding) return null;
        string? name = hurt.Role switch
        {
            InjuryRole.Pitcher => now.Pitcher.Name,
            InjuryRole.Fielder when hurt.Position is Position p && now.Fielders.TryGetValue(p, out var who) => who.Name,
            _ => null,
        };
        return name is not null && name != hurt.PlayerName ? name : null;
    }

    /// <summary>The start of the game: both teams come out, the lineups line up, and the home team takes the field.</summary>
    public static PlayScript PlayBall(GameState game) => Scenes.Intro(game);

    /// <summary>The catcher and the infielders gather around the pitcher on the mound.</summary>
    public static PlayScript MoundVisit(PrePlaySnapshot at) => new Scenes(at).MoundVisit();

    /// <summary>
    /// The home team wins in its last at-bat: it pours out of the dugout and mobs the plate.
    /// <paramref name="lastPlay"/> is the situation before the winning play.
    /// </summary>
    public static PlayScript WalkOff(PrePlaySnapshot lastPlay, GameState game) => new Scenes(lastPlay).WalkOff(game);

    /// <summary>The home team batted last and went ahead, which ends the game on the spot.</summary>
    public static bool IsWalkOff(GameState game) =>
        game is { Status: GameStatus.Final, EndedByRain: false, IsTopOfInning: false }
        && game.HomeTotalScore > game.VisitorTotalScore;

    /// <summary>The lines of the final score card: the result, the line score and the pitchers' decisions.</summary>
    public static IReadOnlyList<string> FinalCard(GameState game)
    {
        var lines = new List<string> { game.EndedByRain ? "FINAL (RAIN)" : "FINAL" };
        int innings = Math.Max(9, Math.Max(game.VisitorScoreByInning.Count, game.HomeScoreByInning.Count));
        string Row(bool visitor)
        {
            var team = game.TeamFor(visitor);
            string total = (visitor ? game.VisitorTotalScore : game.HomeTotalScore).ToString();
            return $"{Abbreviation(team),-4}{total,3}";
        }
        lines.Add(Row(true));
        lines.Add(Row(false));

        var decisions = new List<string>();
        foreach (var (code, label) in new[] { ("W", "W"), ("L", "L"), ("S", "SV") })
        {
            var who = new[] { game.Box.Visitor, game.Box.Home }
                .SelectMany(b => b.Pitchers).FirstOrDefault(p => p.Decision == code);
            if (who is not null) decisions.Add($"{label}: {Surname(who.Name)}");
        }
        if (decisions.Count > 0) lines.Add(string.Join("  ", decisions));
        _ = innings;
        return lines;
    }

    /// <summary>The final score card as a script of zero length, for showing it for as long as the game is over.</summary>
    public static PlayScript FinalScene(PrePlaySnapshot situation, GameState game)
    {
        var setup = PlayChoreographer.BuildSetup(situation);
        return setup with { Cues = [.. setup.Cues, new Cue(0, CueKind.Card, string.Join("\n", FinalCard(game)))] };
    }

    private static string Abbreviation(Team team) =>
        string.IsNullOrWhiteSpace(team.Abbreviation) ? team.Nickname.ToUpperInvariant() : team.Abbreviation.ToUpperInvariant();

    private static string Surname(string name) => name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? name;

    internal static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}TH",
        _ => (n % 10) switch { 1 => $"{n}ST", 2 => $"{n}ND", 3 => $"{n}RD", _ => $"{n}TH" },
    };
}

/// <summary>The scenes themselves. One instance is built around the situation the scene ends in (or, for a walk-off, starts from).</summary>
internal sealed class Scenes
{
    // Everyone walks in from, and out to, a point past the edge of the picture, so nobody pops in or out.
    private const double OffScreen = 150;
    private const double BullpenX = 200;
    private const double BullpenY = 6;
    private const double GateX = 30;        // where people pass between the field and the dugouts: the open ground behind home plate
    private const double MaxTravel = 2.4;   // the longest anyone takes to get anywhere, in seconds
    private const double Hold = 0.4;        // time left after the last thing happens

    private readonly PrePlaySnapshot _snap;
    private readonly ScriptBuilder _s = new();
    private readonly IReadOnlyDictionary<Position, FieldPoint> _spots;
    private readonly string _offense, _defense;

    public Scenes(PrePlaySnapshot snap)
    {
        _snap = snap;
        _spots = Staging.SpotsFor(snap);
        _offense = snap.IsTop ? TeamSide.Visitor : TeamSide.Home;
        _defense = snap.IsTop ? TeamSide.Home : TeamSide.Visitor;
    }

    // ── Places ───────────────────────────────────────────────────────────────

    // The home team's dugout is on the third-base side (left of the picture), the visitors' on the first-base side.
    private static double Side(string team) => team == TeamSide.Home ? -1 : 1;

    // The dugouts and the bullpens are past the edge of the picture and behind the wall that runs along each foul line.
    private static FieldPoint Dugout(string team, int i = 0) => new(Side(team) * OffScreen, 4 + 2 * (i % 10));
    private static FieldPoint Bullpen(string team) => new(Side(team) * BullpenX, BullpenY);

    // The wall along a foul line stands 31 feet outside it and stops short of home plate. Anyone behind it has
    // to come and go by the open ground at the end of it, and never over it or through the stands.
    private static bool BehindTheWall(FieldPoint p) => Math.Abs(p.X) > 50 && Math.Abs(p.X) - p.Y > 31;
    private static FieldPoint Gate(double x) => new(Math.Sign(x) * GateX, 0);

    private static List<FieldPoint> RouteBetween(FieldPoint from, FieldPoint to)
    {
        var route = new List<FieldPoint> { from };
        bool leaving = BehindTheWall(from), entering = BehindTheWall(to);
        if (!(leaving && entering && Math.Sign(from.X) == Math.Sign(to.X)))
        {
            if (leaving) route.Add(Gate(from.X));
            if (entering) route.Add(Gate(to.X));
        }
        route.Add(to);
        return route;
    }

    private double BatterX(BatHand hand) => hand == BatHand.Left ? 3.5 : -3.5;

    private static string IdOf(Position pos) => pos switch
    {
        Position.C => "catcher",
        Position.P => "pitcher",
        _ => $"fielder-{pos}",
    };

    // ── Moving people ────────────────────────────────────────────────────────

    private void Add(string id, ActorKind kind, string? label, string team, FieldPoint at, string pose = Poses.Stand, string? hand = null, string? stance = null) =>
        _s.AddActor(new Actor(id, kind, label, team, hand, stance), at, pose);

    // Gets someone to a point: no sooner than `start`, taking about as long as the distance needs (never long).
    // He goes by the gate if a dugout is at either end. Returns when he gets there.
    private double Go(string id, double start, FieldPoint to, string endPose = Poses.Stand, double? seconds = null)
    {
        double t0 = Math.Max(start, _s.TimeOf(id));
        var from = _s.PointOf(id);
        var route = RouteBetween(from, to);
        double length = 0;
        for (int k = 1; k < route.Count; k++) length += route[k - 1].DistanceTo(route[k]);
        double duration = seconds ?? Math.Clamp(length / 70, 0.5, MaxTravel);

        _s.Move(id, t0, from, pose: Poses.Run);
        double walked = 0;
        for (int k = 1; k < route.Count; k++)
        {
            walked += route[k - 1].DistanceTo(route[k]);
            _s.Move(id, t0 + (length > 0 ? duration * walked / length : duration), route[k], pose: k == route.Count - 1 ? endPose : Poses.Run);
        }
        return t0 + duration;
    }

    // Jumping up and down with the arms up, from `start` for `seconds`.
    private void Celebrate(string id, double start, double seconds)
    {
        var at = _s.PointOf(id);
        double t = Math.Max(start, _s.TimeOf(id));
        _s.Move(id, t, at, pose: Poses.Cheer);
        for (double k = 0.25; k <= seconds + 1e-9; k += 0.25)
            _s.Move(id, t + k, at, z: Math.Round(k / 0.25) % 2 == 1 ? 1.6 : 0, pose: Poses.Cheer);
    }

    /// <summary>
    /// Puts everyone in the situation on the field where the snapshot has them. Anyone in <paramref name="spawn"/>
    /// starts at the point given instead. Returns where each person belongs (so the caller can bring the others in).
    /// </summary>
    private Dictionary<string, FieldPoint> Cast(PrePlaySnapshot snap, IReadOnlyDictionary<string, FieldPoint>? spawn = null,
                                               bool pitcher = true, bool batter = true, bool runners = true,
                                               string batterPose = Poses.Stand, ISet<string>? omit = null)
    {
        var home = new Dictionary<string, FieldPoint>();
        var spots = Staging.SpotsFor(snap);
        string offense = snap.IsTop ? TeamSide.Visitor : TeamSide.Home;
        string defense = snap.IsTop ? TeamSide.Home : TeamSide.Visitor;

        void Put(string id, ActorKind kind, string? label, string team, FieldPoint belongs, string pose = Poses.Stand, string? hand = null, string? stance = null)
        {
            if (omit is not null && omit.Contains(id)) return;
            home[id] = belongs;
            Add(id, kind, label, team, spawn is not null && spawn.TryGetValue(id, out var start) ? start : belongs, pose, hand, stance);
        }

        if (pitcher) Put("pitcher", ActorKind.Pitcher, snap.Pitcher.Name, defense, FieldGeometry.Mound, hand: Hands.Of(snap.Pitcher.ThrowHand));
        if (snap.Fielders.TryGetValue(Position.C, out var catcher))
            Put("catcher", ActorKind.Catcher, catcher.Name, defense, spots[Position.C], hand: Hands.Of(catcher.ThrowHand));
        foreach (var pos in FieldGeometry.FieldingPositions)
            if (snap.Fielders.TryGetValue(pos, out var who))
                Put($"fielder-{pos}", ActorKind.Fielder, who.Name, defense, spots[pos], hand: Hands.Of(who.ThrowHand));

        if (batter)
            Put("batter", ActorKind.Batter, snap.Batter.Name, offense, new FieldPoint(BatterX(snap.BatsFrom), 0), batterPose,
                Hands.Of(snap.BatsFrom), Stances.Of(snap.Batter, snap.Pitcher.ThrowHand));
        if (runners)
            foreach (var (runner, number) in new[] { (snap.First, 1), (snap.Second, 2), (snap.Third, 3) })
                if (runner is not null)
                    Put($"runner-{runner.Player.Id}", ActorKind.Runner, runner.Player.Name, offense, FieldGeometry.BaseSpot(number));
        return home;
    }

    // Brings everyone in `spawn` from where he started to where he belongs, one after another. Returns the last arrival.
    private double Arrive(IReadOnlyDictionary<string, FieldPoint> belongs, IEnumerable<string> ids, double start, double stagger)
    {
        double last = start;
        int i = 0;
        foreach (var id in ids)
            last = Math.Max(last, Go(id, start + stagger * i++, belongs[id]));
        return last;
    }

    private PlayScript Finish(string name, double? minimum = null)
    {
        var script = _s.Build(name, Hold);
        return minimum is double m && script.Duration < m ? script with { Duration = m } : script;
    }

    public PlayScript AlignmentChange(PrePlaySnapshot before)
    {
        Cast(before);
        var oldSpots = Staging.SpotsFor(before);
        double end = 0;
        foreach (var pos in FieldGeometry.FieldingPositions)
        {
            if (!_snap.Fielders.ContainsKey(pos) || oldSpots[pos] == _spots[pos]) continue;
            double distance = oldSpots[pos].DistanceTo(_spots[pos]);
            end = Math.Max(end, Go($"fielder-{pos}", 0, _spots[pos], seconds: Math.Clamp(distance / 70, 0.45, 1.2)));
        }
        _s.Cue(0, CueKind.Banner, _snap.Alignment switch
        {
            DefensiveAlignment.InfieldIn => "INFIELD IN",
            DefensiveAlignment.CornersIn => "CORNERS IN",
            DefensiveAlignment.GuardLines => "GUARD THE LINES",
            DefensiveAlignment.ShiftPull => "SHIFT: PULL SIDE",
            DefensiveAlignment.ShiftOpposite => "SHIFT: OPPOSITE FIELD",
            _ => "NORMAL FIELD POSITION",
        });
        return Finish("AlignmentChange", end + 0.15);
    }

    private static string Situation(PrePlaySnapshot s) =>
        $"{(s.IsTop ? "TOP" : "BOTTOM")} OF INNING {s.Inning}\n{s.Batter.Name} AT BAT";

    // ── A new half-inning ────────────────────────────────────────────────────

    /// <summary>The side that was in the field leaves, the side that was batting takes the field.</summary>
    public PlayScript HalfInning(PrePlaySnapshot before)
    {
        string leaving = before.IsTop ? TeamSide.Home : TeamSide.Visitor;      // the old defense
        string arriving = _defense;                                             // the new defense: the old offense

        // The new defense and the new batter start in their dugout.
        var spawn = new Dictionary<string, FieldPoint>();
        int n = 0;
        spawn["pitcher"] = Dugout(arriving, n++);
        if (_snap.Fielders.ContainsKey(Position.C)) spawn["catcher"] = Dugout(arriving, n++);
        foreach (var pos in FieldGeometry.FieldingPositions)
            if (_snap.Fielders.ContainsKey(pos)) spawn[$"fielder-{pos}"] = Dugout(arriving, n++);
        spawn["batter"] = Dugout(leaving);

        // The old defense stands where it did.
        var oldSpots = Staging.SpotsFor(before);
        var oldIds = new List<string>();
        Add("old-pitcher", ActorKind.Pitcher, before.Pitcher.Name, leaving, FieldGeometry.Mound);
        oldIds.Add("old-pitcher");
        if (before.Fielders.TryGetValue(Position.C, out var c))
        {
            Add("old-catcher", ActorKind.Catcher, c.Name, leaving, oldSpots[Position.C]);
            oldIds.Add("old-catcher");
        }
        foreach (var pos in FieldGeometry.FieldingPositions)
            if (before.Fielders.TryGetValue(pos, out var who))
            {
                Add($"old-{pos}", ActorKind.Fielder, who.Name, leaving, oldSpots[pos]);
                oldIds.Add($"old-{pos}");
            }

        var belongs = Cast(_snap, spawn);

        _s.Cue(0.1, CueKind.Banner, $"END OF THE {(before.IsTop ? "TOP" : "BOTTOM")} OF THE {SceneChoreographer.Ordinal(before.Inning)}");

        // Out they go (the near ones first), and in they come.
        int i = 0;
        double out_ = 0;
        foreach (var id in oldIds.OrderBy(id => _s.PointOf(id).DistanceFromHome))
            out_ = Math.Max(out_, Go(id, 0.2 + 0.08 * i++, Dugout(leaving, i)));

        var order = belongs.Keys.Where(k => k != "batter").OrderBy(k => belongs[k].DistanceFromHome).ToList();
        double in_ = Arrive(belongs, order, 0.9, 0.08);
        double up = Go("batter", Math.Max(1.6, in_ - 1.2), belongs["batter"]);

        _s.Cue(Math.Max(in_, up), CueKind.Banner, Situation(_snap));
        return Finish("HalfInning");
    }

    // ── A new pitcher ────────────────────────────────────────────────────────

    /// <summary>The manager comes out, the new pitcher jogs in from the bullpen, and the old one leaves with the manager.</summary>
    public PlayScript PitchingChange(PrePlaySnapshot before)
    {
        var spawn = new Dictionary<string, FieldPoint> { ["pitcher"] = Bullpen(_defense) };
        // A switch hitter who is still at the plate steps over to the other side when the pitcher's arm changes.
        bool crossing = before.Batter.Id == _snap.Batter.Id && before.BatsFrom != _snap.BatsFrom;
        if (crossing) spawn["batter"] = new FieldPoint(BatterX(before.BatsFrom), 0);
        var belongs = Cast(_snap, spawn);

        Add("old-pitcher", ActorKind.Pitcher, before.Pitcher.Name, _defense, FieldGeometry.Mound);
        Add("manager", ActorKind.Teammate, "Manager", _defense, Dugout(_defense));

        _s.Cue(0.1, CueKind.Banner, "PITCHING CHANGE");
        double manager = Go("manager", 0.3, new FieldPoint(5, 56));
        double reliever = Go("pitcher", 0.4, new FieldPoint(-4, 63));
        double met = Math.Max(manager, reliever);

        // The ball changes hands, then the old pitcher and the manager go off together.
        _s.Move("old-pitcher", Math.Max(_s.TimeOf("old-pitcher"), met - 0.3), FieldGeometry.Mound, pose: Poses.Throw);
        double leave = met + 0.5;
        double gone = Math.Max(Go("old-pitcher", leave, Dugout(_defense, 1)), Go("manager", leave + 0.1, Dugout(_defense, 2)));
        Go("pitcher", leave + 0.3, belongs["pitcher"]);

        _s.Cue(met, CueKind.Banner, $"{_snap.Pitcher.Name} IN FOR {before.Pitcher.Name}".ToUpperInvariant());
        double settled = gone;
        if (crossing)
        {
            // Out of the box, round behind the catcher, into the other one.
            double start = met + 0.2;
            _s.Cue(start, CueKind.Banner, $"{_snap.Batter.Name} SWITCHES TO BAT {(_snap.BatsFrom == BatHand.Left ? "LEFT" : "RIGHT")}".ToUpperInvariant());
            Go("batter", start, new FieldPoint(0, -10), seconds: 0.7);
            settled = Math.Max(settled, Go("batter", start + 0.7, belongs["batter"], seconds: 0.7));
        }
        _s.Cue(settled, CueKind.Banner, Situation(_snap));
        return Finish("PitchingChange");
    }

    // ── Pinch hitters, pinch runners and new fielders ────────────────────────

    /// <summary>Anyone new in the batter's box, on a base or in the field walks out and the player he replaces walks off. Null if nobody is.</summary>
    public PlayScript? Substitutions(PrePlaySnapshot before)
    {
        var changes = new List<(string OldId, string NewId, string Team, FieldPoint Dugout, string Line)>();

        if (before.Batter.Id != _snap.Batter.Id)
            changes.Add(("old-batter", "batter", _offense, Dugout(_offense),
                $"PINCH HITTER {_snap.Batter.Name} FOR {before.Batter.Name}"));

        foreach (var (old, now, number) in new[] { (before.First, _snap.First, 1), (before.Second, _snap.Second, 2), (before.Third, _snap.Third, 3) })
            if (old is not null && now is not null && old.Player.Id != now.Player.Id)
                changes.Add(($"old-runner-{old.Player.Id}", $"runner-{now.Player.Id}", _offense, Dugout(_offense),
                    $"PINCH RUNNER {now.Player.Name} FOR {old.Player.Name}"));

        foreach (var (pos, now) in _snap.Fielders)
            if (before.Fielders.TryGetValue(pos, out var old) && old.Id != now.Id && !OnField(_snap, old.Id))
                changes.Add(($"old-{pos}", IdOf(pos), _defense, Dugout(_defense),
                    $"{now.Name} REPLACES {old.Name}"));

        if (changes.Count == 0) return null;

        var spawn = changes.ToDictionary(c => c.NewId, c => c.Dugout);
        var belongs = Cast(_snap, spawn);

        var oldSpots = Staging.SpotsFor(before);
        foreach (var c in changes)
        {
            var oldPlayer = c.OldId switch
            {
                "old-batter" => before.Batter,
                var id when id.StartsWith("old-runner-") => RunnerNamed(before, id),
                var id => before.Fielders[Enum.Parse<Position>(id["old-".Length..])],
            };
            var at = c.OldId switch
            {
                "old-batter" => new FieldPoint(BatterX(before.BatsFrom), 0),
                var id when id.StartsWith("old-runner-") => BaseOf(before, oldPlayer.Id),
                var id => oldSpots[Enum.Parse<Position>(id["old-".Length..])],
            };
            var kind = c.OldId == "old-batter" ? ActorKind.Batter : c.OldId.StartsWith("old-runner-") ? ActorKind.Runner : ActorKind.Fielder;
            Add(c.OldId, kind, oldPlayer.Name, c.Team, at);
        }

        double t = 0.3;
        double end = 0;
        int i = 0;
        foreach (var c in changes)
        {
            _s.Cue(t, CueKind.Banner, c.Line.ToUpperInvariant());
            double arrive = Go(c.NewId, t, belongs[c.NewId]);
            double leave = Go(c.OldId, t + 0.2, Dugout(c.Team, ++i));
            end = Math.Max(end, Math.Max(arrive, leave));
            t = arrive + 0.3;
        }
        _s.Cue(end, CueKind.Banner, Situation(_snap));
        return Finish("Substitutions");
    }

    // Still in the game on the field (a swap of positions, not a substitution).
    private static bool OnField(PrePlaySnapshot s, int playerId) =>
        s.Pitcher.Id == playerId || s.Fielders.Values.Any(p => p.Id == playerId);

    private static Player RunnerNamed(PrePlaySnapshot s, string oldId)
    {
        int id = int.Parse(oldId["old-runner-".Length..]);
        return new[] { s.First, s.Second, s.Third }.First(r => r?.Player.Id == id)!.Player;
    }

    private static FieldPoint BaseOf(PrePlaySnapshot s, int playerId) =>
        s.First?.Player.Id == playerId ? FieldGeometry.First
        : s.Second?.Player.Id == playerId ? FieldGeometry.Second
        : FieldGeometry.Third;

    // ── A visit to the mound ─────────────────────────────────────────────────

    public PlayScript MoundVisit()
    {
        var belongs = Cast(_snap);
        _s.Cue(0.1, CueKind.Banner, "MOUND VISIT");

        var visitors = new[] { Position.C, Position.First, Position.Second, Position.Short, Position.Third }
            .Where(_snap.Fielders.ContainsKey).ToList();
        double arrived = 0;
        for (int i = 0; i < visitors.Count; i++)
        {
            double angle = (-90 + 360.0 * i / visitors.Count) * Math.PI / 180;
            var ring = new FieldPoint(7 * Math.Cos(angle), 60.5 + 7 * Math.Sin(angle) - 1);
            arrived = Math.Max(arrived, Go(IdOf(visitors[i]), 0.3 + 0.1 * i, ring));
        }
        double back = arrived + 1.5;   // a word with the pitcher
        for (int i = 0; i < visitors.Count; i++)
            Go(IdOf(visitors[i]), back + 0.1 * i, belongs[IdOf(visitors[i])]);

        _s.Cue(back + 1.2, CueKind.Banner, Situation(_snap));
        return Finish("MoundVisit");
    }

    // ── A walk-off ───────────────────────────────────────────────────────────

    public PlayScript WalkOff(GameState game)
    {
        // The batter who won it, where he stands, and the visitors in the field.
        var belongs = Cast(_snap, runners: false, batterPose: Poses.Cheer);
        string visitors = TeamSide.Visitor;

        _s.Cue(0.2, CueKind.Banner, $"{game.HomeTeam.Nickname} WIN!".ToUpperInvariant());
        _s.Cue(0.3, CueKind.Sound, "cheer");
        _s.Cue(0.3, CueKind.Effect, "stand");

        // The whole home team comes out and meets the batter at the plate.
        var plate = FieldGeometry.Home;
        double mob = 0;
        for (int i = 0; i < 9; i++)
        {
            string id = $"mate-{i}";
            Add(id, ActorKind.Teammate, null, TeamSide.Home, Dugout(TeamSide.Home, i));
            double angle = (200 + 20 * i) * Math.PI / 180;
            var spot = new FieldPoint(plate.X + 5 * Math.Cos(angle) * (1 + 0.5 * (i % 2)), -2 + 4 * Math.Sin(angle) * (1 + 0.5 * (i % 2)) + 3);
            mob = Math.Max(mob, Go(id, 0.3 + 0.07 * i, spot));
        }
        for (int i = 0; i < 9; i++) Celebrate($"mate-{i}", mob, 1.5);
        Celebrate("batter", 0.2, mob + 1.5 - 0.2);

        // The visitors trudge off.
        int n = 0;
        foreach (var id in belongs.Keys.Where(k => k != "batter").ToList())
            Go(id, 1.0 + 0.06 * n, Dugout(visitors, n++));

        _s.Cue(mob + 1.4, CueKind.Banner, $"FINAL\n{Abbreviation(game.VisitorTeam)} {game.VisitorTotalScore}  {Abbreviation(game.HomeTeam)} {game.HomeTotalScore}");
        return Finish("WalkOff");
    }

    private static string Abbreviation(Team team) =>
        string.IsNullOrWhiteSpace(team.Abbreviation) ? team.Nickname.ToUpperInvariant() : team.Abbreviation.ToUpperInvariant();

    // ── An ejection ──────────────────────────────────────────────────────────

    public PlayScript Ejection(GameEvent thrownOut)
    {
        string team = thrownOut.Visitor ? TeamSide.Visitor : TeamSide.Home;
        Cast(_snap);
        var plate = new FieldPoint(5, -8);
        Add("manager", ActorKind.Teammate, "Manager", team, Dugout(team));

        _s.Cue(0.1, CueKind.Banner, "THE MANAGER IS COMING OUT");
        double there = Go("manager", 0.2, plate);
        // He argues, arms up and jumping, at the plate.
        Celebrate("manager", there, 1.6);
        double argued = there + 1.6;
        _s.Cue(there, CueKind.Banner, "THE MANAGER ARGUES THE CALL");
        _s.Cue(argued + 0.1, CueKind.Banner, $"{(thrownOut.Visitor ? "VISITING" : "HOME")} MANAGER IS EJECTED!");
        Go("manager", argued + 0.7, Dugout(team, 1));
        return Finish("Ejection");
    }

    // ── An injury ────────────────────────────────────────────────────────────

    public PlayScript Injury(GameEvent hurt, string? replacement)
    {
        string team = hurt.Visitor ? TeamSide.Visitor : TeamSide.Home;
        string pitcherId = "pitcher";
        var omit = new HashSet<string>();
        FieldPoint spot;
        ActorKind kind;
        switch (hurt.Role)
        {
            case InjuryRole.Pitcher:
                omit.Add(pitcherId); spot = FieldGeometry.Mound; kind = ActorKind.Pitcher; break;
            case InjuryRole.Runner:
                spot = FieldGeometry.BaseSpot(Math.Clamp(hurt.Base, 1, 3)); kind = ActorKind.Runner; break;
            case InjuryRole.Batter:
                spot = new FieldPoint(BatterX(_snap.BatsFrom), 0); kind = ActorKind.Batter; break;
            default:
            {
                var pos = hurt.Position ?? Position.Left;
                var spots = Staging.SpotsFor(_snap);
                spot = spots.TryGetValue(pos, out var at) ? at : FieldGeometry.Mound;
                omit.Add(IdOf(pos));
                kind = pos == Position.C ? ActorKind.Catcher : ActorKind.Fielder;
                break;
            }
        }

        Cast(_snap, runners: false, batter: false, omit: omit);
        Add("injured", kind, hurt.PlayerName, team, spot, Poses.Down);
        Add("trainer-0", ActorKind.Teammate, "Trainer", team, Dugout(team, 0));
        Add("trainer-1", ActorKind.Teammate, "Trainer", team, Dugout(team, 1));

        _s.Cue(0.1, CueKind.Banner, $"{hurt.PlayerName} IS DOWN".ToUpperInvariant());
        double a = Go("trainer-0", 0.4, new FieldPoint(spot.X - 3, spot.Y - 2));
        double b = Go("trainer-1", 0.6, new FieldPoint(spot.X + 3, spot.Y - 2));
        double tended = Math.Max(a, b) + 1.4;

        // He gets up, and the trainers walk him off.
        _s.Move("injured", tended, spot, pose: Poses.Stand);
        double off = tended + 0.3;
        double gone = Math.Max(Go("injured", off, Dugout(team, 2), seconds: 2.4),
                      Math.Max(Go("trainer-0", off, Dugout(team, 3), seconds: 2.4), Go("trainer-1", off, Dugout(team, 4), seconds: 2.4)));
        _s.Cue(off, CueKind.Banner, $"{hurt.PlayerName} MUST LEAVE THE GAME".ToUpperInvariant());

        if (replacement is not null && hurt.Role is InjuryRole.Pitcher or InjuryRole.Fielder)
        {
            Add("replacement", ActorKind.Fielder, replacement, team, Dugout(team, 5));
            double in_ = Go("replacement", off + 0.8, spot);
            _s.Cue(in_, CueKind.Banner, $"{replacement} COMES IN".ToUpperInvariant());
        }
        return Finish("Injury");
    }

    // ── A rain delay ─────────────────────────────────────────────────────────

    public PlayScript RainDelay(GameEvent delay)
    {
        var belongs = Cast(_snap);
        var teams = new Dictionary<string, string>();
        foreach (var actor in new[] { "pitcher" }.Concat(belongs.Keys.Where(k => k != "pitcher")))
            teams[actor] = actor == "batter" || actor.StartsWith("runner-") ? _offense : _defense;

        _s.Cue(0.1, CueKind.Banner, "RAIN DELAY");
        _s.Cue(0.3, CueKind.Effect, "rain");

        // Everyone heads for cover.
        int i = 0;
        double inside = 0;
        foreach (var (id, team) in teams)
            inside = Math.Max(inside, Go(id, 0.8 + 0.05 * i, Dugout(team, i++)));

        _s.Cue(inside + 0.2, CueKind.Banner, $"RAIN DELAY: {delay.Minutes} MINUTES");
        double stops = inside + 2.2;
        _s.Cue(stops, CueKind.Effect, "clear");

        // The rain stops and they come back.
        i = 0;
        double back = 0;
        foreach (var (id, _) in teams.OrderBy(t => belongs[t.Key].DistanceFromHome))
            back = Math.Max(back, Go(id, stops + 0.5 + 0.05 * i++, belongs[id]));
        _s.Cue(back, CueKind.Banner, Situation(_snap));
        return Finish("RainDelay");
    }

    // ── The start of the game ────────────────────────────────────────────────

    public static PlayScript Intro(GameState game)
    {
        var snap = PrePlaySnapshot.Capture(game);
        // A first pass only to learn where everyone belongs; the scene itself is built on a fresh builder.
        var belongs = new Scenes(snap).Cast(snap, runners: false);
        return new Scenes(snap).BuildIntro(game, belongs);
    }

    // The figure a home player becomes when the game starts: the pitcher or the man at his position (null if he does not play in the field).
    private string? HomeFigureOf(int playerId)
    {
        if (_snap.Pitcher.Id == playerId) return "pitcher";
        foreach (var (pos, who) in _snap.Fielders)
            if (who.Id == playerId && pos != Position.P) return IdOf(pos);
        return null;
    }

    private PlayScript BuildIntro(GameState game, Dictionary<string, FieldPoint> belongs)
    {
        // The home fielders start in their dugout and the first visiting batter in his.
        var spawn = new Dictionary<string, FieldPoint>();
        int n = 0;
        foreach (var id in belongs.Keys.Where(k => k != "batter")) spawn[id] = Dugout(TeamSide.Home, n++);
        spawn["batter"] = Dugout(TeamSide.Visitor);
        Cast(_snap, spawn, runners: false);

        // Each team's batting order stands along its foul line (home on the third-base side, visitors on the first).
        var lines = new[] { (Team: TeamSide.Visitor, Players: game.LineupFor(true).Select(id => game.VisitorTeam.Roster.First(p => p.Id == id)).ToList()),
                            (Team: TeamSide.Home, Players: game.LineupFor(false).Select(id => game.HomeTeam.Roster.First(p => p.Id == id)).ToList()) };
        double lined = 0;
        foreach (var (team, players) in lines)
            for (int i = 0; i < players.Count; i++)
            {
                double y = 24 + 13 * i;
                string id = $"intro-{team}-{i}";
                Add(id, ActorKind.Teammate, players[i].Name, team, Dugout(team, i));
                lined = Math.Max(lined, Go(id, 0.5 + 0.12 * i, new FieldPoint(Side(team) * (y + 9), y)));
            }
        _s.Cue(0.2, CueKind.Banner, $"{game.VisitorTeam.DisplayName}\nAT\n{game.HomeTeam.DisplayName}".ToUpperInvariant());
        _s.Cue(lined + 0.3, CueKind.Banner, string.IsNullOrWhiteSpace(game.Stadium.Name) ? "THE LINEUPS" : $"TODAY AT {game.Stadium.Name}".ToUpperInvariant());

        // The break-up: a man on the line who has a place in the first pitch (a home fielder, the first visiting
        // batter) goes straight from the line to it, and everyone else goes back to the dugout. The same man
        // is on the line and in the field: the walker hands over to the field figure the instant he gets there.
        double back = lined + 1.6;
        var handedOver = new HashSet<string>();
        double ready = back;
        foreach (var (team, players) in lines)
            for (int i = 0; i < players.Count; i++)
            {
                string walker = $"intro-{team}-{i}";
                string? becomes = team == TeamSide.Home ? HomeFigureOf(players[i].Id)
                                : i == 0 && players[i].Id == _snap.Batter.Id ? "batter" : null;
                if (becomes is null || !belongs.ContainsKey(becomes))
                {
                    Go(walker, back + 0.05 * i, Dugout(team, i));
                    continue;
                }

                Go(walker, back + 0.05 * i, belongs[becomes]);
                double there = _s.TimeOf(walker);
                var away = _s.PointOf(becomes);
                _s.Move(walker, there, Dugout(team, i), pose: Poses.Stand);
                _s.Move(becomes, there, away);
                _s.Move(becomes, there, belongs[becomes], pose: Poses.Stand);
                handedOver.Add(becomes);
                ready = Math.Max(ready, there);
            }

        // Anyone else the first pitch needs (a pitcher who does not bat, say) comes from the dugout.
        var order = belongs.Keys.Where(id => !handedOver.Contains(id)).OrderBy(id => belongs[id].DistanceFromHome).ToList();
        ready = Math.Max(ready, Arrive(belongs, order, back + 0.3, 0.08));

        _s.Cue(ready, CueKind.Banner, "PLAY BALL!\n" + Situation(_snap));
        _s.Cue(ready, CueKind.Sound, "playball");
        return Finish("PlayBall", ready + 1.0);
    }
}
