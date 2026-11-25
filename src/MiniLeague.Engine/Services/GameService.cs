using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Services;

/// <summary>
/// Orchestrates the game loop: applies play results, advances innings, keeps the box score,
/// rolls the optional random events, and determines when the computer manager acts automatically.
/// </summary>
public class GameService
{
    private readonly AtBatResolver _resolver;
    private readonly ComputerManager _cpu;
    private readonly ManagerCatalog? _catalog;
    // One manager per side, built on first use from the ids in the game state (see ManagerFor).
    private ComputerManager? _visitorCpu, _homeCpu;
    private string? _visitorCpuId, _homeCpuId;
    private readonly Random _events;
    private readonly List<GameMoment> _moments = [];
    private readonly List<GameEvent> _happenings = [];

    public GameState? State { get; private set; }

    /// <summary>A human manages the batting side for the next plate appearance.</summary>
    public bool OffenseIsHuman => State is { Status: GameStatus.InProgress }
        && (State.IsTopOfInning ? State.VisitorControl : State.HomeControl) == ManagerControl.Human;

    /// <summary>A human manages the fielding side for the next plate appearance.</summary>
    public bool DefenseIsHuman => State is { Status: GameStatus.InProgress }
        && (State.IsTopOfInning ? State.HomeControl : State.VisitorControl) == ManagerControl.Human;

    /// <summary>The game is waiting on at least one human decision.</summary>
    public bool IsHumanTurn => OffenseIsHuman || DefenseIsHuman;

    /// <summary>
    /// The moments that have happened since this was last called, oldest first (e.g. the seventh-inning
    /// stretch, a rally, a new pitcher). Reading them clears the list.
    /// </summary>
    public IReadOnlyList<GameMoment> TakeMoments()
    {
        var taken = _moments.ToList();
        _moments.Clear();
        return taken;
    }

    /// <summary>
    /// The injuries, ejections and rain delays that have happened since this was last called, oldest first,
    /// so that they can be shown. Reading them clears the list.
    /// </summary>
    public IReadOnlyList<GameEvent> TakeEvents()
    {
        var taken = _happenings.ToList();
        _happenings.Clear();
        return taken;
    }

    /// <param name="cpu">The computer manager for any side that has not been given a persona of its own.</param>
    /// <param name="eventRng">Randomness for injuries, weather and ejections (seed it for repeatable games).</param>
    /// <param name="catalog">Where to find the persona a side was given; without one every side uses <paramref name="cpu"/>.</param>
    /// <param name="managerSeed">
    /// Seeds the randomness of each side's persona manager, so a computer-versus-computer game can be replayed exactly.
    /// Without it they use shared randomness, as a real game should.
    /// </param>
    public GameService(AtBatResolver resolver, ComputerManager cpu, Random? eventRng = null, ManagerCatalog? catalog = null,
        int? managerSeed = null)
    {
        _managerSeed = managerSeed;
        _resolver = resolver;
        _cpu = cpu;
        _events = eventRng ?? Random.Shared;
        _catalog = catalog;
    }

    /// <summary>
    /// The computer manager for one side. WHY: each side can have its own persona, so no decision may go through a
    /// single shared manager. The manager is rebuilt if the side's id changes (a new or resumed game).
    /// </summary>
    private readonly int? _managerSeed;

    private ComputerManager ManagerFor(bool visitor)
    {
        string? id = visitor ? State!.VisitorManagerId : State!.HomeManagerId;
        if (id is null || _catalog is null) return _cpu;

        ref var cached = ref (visitor ? ref _visitorCpu : ref _homeCpu);
        ref var cachedId = ref (visitor ? ref _visitorCpuId : ref _homeCpuId);
        if (cached is null || cachedId != id)
        {
            // WHY: each side gets its own stream (seed, seed + 1), so a visitor's tie-break never shifts the home side's.
            cached = _catalog.Create(id, _managerSeed is int seed ? new Random(seed + (visitor ? 0 : 1)) : null);
            cachedId = id;
        }
        return cached;
    }

    public void StartGame(Team visitor, Team home,
        ManagerControl visitorControl = ManagerControl.Human,
        ManagerControl homeControl = ManagerControl.Computer,
        Stadium? stadium = null,
        IReadOnlyList<LineupSlot>? visitorLineupSlots = null,
        IReadOnlyList<LineupSlot>? homeLineupSlots = null,
        GameOptions? options = null,
        int? visitorStarterId = null,
        int? homeStarterId = null,
        string? visitorManagerId = null,
        string? homeManagerId = null)
    {
        var gameOptions = options ?? GameOptions.None;
        // WHY: both teams play by one rule, the home team's (see DesignatedHitterRules). Without a designated hitter the
        // starting pitcher is in the batting order, so the starters are chosen before the lineups.
        bool designatedHitter = DesignatedHitterRules.Applies(gameOptions.DesignatedHitter, home);
        var visitorStarter = ResolveStarter(visitor, visitorStarterId);
        var homeStarter = ResolveStarter(home, homeStarterId);

        // Each game works on its own copy so substitutions never alter the team.
        var visitorSlots = ResolveLineup(visitor, visitorLineupSlots, designatedHitter, visitorStarter);
        var homeSlots = ResolveLineup(home, homeLineupSlots, designatedHitter, homeStarter);

        State = new GameState
        {
            VisitorTeam = visitor,
            HomeTeam = home,
            Stadium = stadium ?? Stadium.Generic,
            Options = gameOptions,
            DHRule = designatedHitter,
            VisitorControl = visitorControl,
            HomeControl = homeControl,
            VisitorManagerId = visitorManagerId,
            HomeManagerId = homeManagerId,
            VisitorLineup = visitorSlots.Select(x => x.PlayerId).ToList(),
            HomeLineup = homeSlots.Select(x => x.PlayerId).ToList(),
            VisitorPositions = visitorSlots.ToDictionary(x => x.PlayerId, x => x.Position),
            HomePositions = homeSlots.ToDictionary(x => x.PlayerId, x => x.Position),
            VisitorPitcher = new ActivePitcher { Player = visitorStarter },
            HomePitcher = new ActivePitcher { Player = homeStarter },
            VisitorBullpen = NewBullpen(visitor, visitorStarter),
            HomeBullpen = NewBullpen(home, homeStarter),
            Status = GameStatus.InProgress,
            CurrentInning = 1,
            IsTopOfInning = true,
        };
        State.VisitorUsed.Add(visitorStarter.Id);
        State.HomeUsed.Add(homeStarter.Id);
        BoxScoreRecorder.StartGame(State);
        State.LastHomeWinProbability = GameRecaps.StartingWinProbability(State);
        LastPrePlay = null;
        _moments.Clear();
        _happenings.Clear();
    }

    // Relievers and closers start the game resting. Other starters are in the rotation, not available.
    private static List<WarmingPitcher> NewBullpen(Team team, Player starter) =>
        team.Bullpen.Where(p => p.Id != starter.Id)
            .Select(p => new WarmingPitcher { Player = p }).ToList();

    /// <summary>Restores a game from a saved snapshot. Throws InvalidOperationException if it no longer fits the rosters.</summary>
    public void Resume(GameSnapshot snapshot, Func<int, Team?> findTeam, Func<string, Stadium?>? findStadium = null)
    {
        State = GameSnapshots.Restore(snapshot, findTeam, findStadium);
        LastPrePlay = null;
        _moments.Clear();
        _happenings.Clear();
    }

    /// <summary>
    /// The situation just before the most recent play (after any substitutions it made), for showing that play.
    /// Null before the first play and after a game is started or resumed.
    /// </summary>
    public PrePlaySnapshot? LastPrePlay { get; private set; }

    /// <summary>Resolve the current at-bat using the provided decisions.</summary>
    public PlayResult ExecuteAtBat(OffensiveDecision offense, DefensiveDecision defense)
    {
        var s = InProgress();
        if (s.PendingInjury is not null)
            throw new InvalidOperationException("Replace the injured player before play continues.");

        ApplyOffensiveSubstitutions(offense);
        if (defense.RelieverPlayerId is int relieverId)
            ChangePitcher(!s.IsTopOfInning, relieverId);

        // The chosen alignment is what the base-running logic reads for double plays.
        if (s.IsTopOfInning) s.HomeAlignment = defense.Alignment;
        else s.VisitorAlignment = defense.Alignment;

        bool top = s.IsTopOfInning;
        var batter = s.CurrentBatter;
        LastPrePlay = PrePlaySnapshot.Capture(s);
        int inningBefore = s.CurrentInning;
        var result = _resolver.Resolve(s, offense, defense);
        result.Pitch = defense.PitchType;
        ApplyPlay(result);

        if (s.Status == GameStatus.InProgress)
        {
            MaybeEjectManager(result, top);
            MaybeInjurePlayer(result, top, batter);
            // A defensive replacement is made as a side takes the field, so only when the half-inning has just changed.
            if (s.CurrentInning != inningBefore || s.IsTopOfInning != top)
            {
                EnforcePitcherAfterPinchHit();
                ManageComputerDefense();
            }
            ManageComputerPitching();
        }
        return result;
    }

    // Applies a resolved play and records it in the log and on the win-probability track.
    // internal so recap and flow rules can be tested with exact results
    internal void ApplyPlay(PlayResult result)
    {
        var s = State!;
        int inning = s.CurrentInning;
        bool top = s.IsTopOfInning;
        double before = s.LastHomeWinProbability;

        ApplyResult(result);
        LogPlay(result, inning, top);
        GameRecaps.RecordPlay(s, result, inning, top, before);
    }

    /// <summary>
    /// Play the next plate appearance. Pass the decision(s) made by human manager(s); any
    /// side not supplied is decided by the computer. A human-managed side must supply its decision.
    /// </summary>
    public PlayResult ExecutePlay(OffensiveDecision? humanOffense = null, DefensiveDecision? humanDefense = null)
    {
        var s = InProgress();
        if (humanOffense is null && OffenseIsHuman)
            throw new InvalidOperationException("The batting manager must make a decision.");
        if (humanDefense is null && DefenseIsHuman)
            throw new InvalidOperationException("The fielding manager must make a decision.");

        OffensiveDecision offense;
        DefensiveDecision defense;
        // WHY: both managers value the same situation (the same batter, pitcher and bases), so what one works out the other
        // can reuse. The scope ends before the play is resolved: from then on the situation is different.
        using (ManagerDecisionModel.Decision(s))
        {
            offense = humanOffense ?? ManagerFor(s.IsTopOfInning).MakeOffensiveDecision(s);
            // WHY: the batting manager declares a pinch hitter before the pitch. The computer defense must select
            // its pitch, alignment, and intentional-walk response for that incoming batter, not the outgoing one.
            var plannedBatter = offense.PinchHitterPlayerId is int pinchId
                ? s.OffenseTeam.Roster.FirstOrDefault(p => p.Id == pinchId) : null;
            defense = humanDefense ?? ManagerFor(!s.IsTopOfInning).MakeDefensiveDecision(s, plannedBatter);
        }
        LastOffense = offense;
        LastDefense = defense;
        return ExecuteAtBat(offense, defense);
    }

    /// <summary>The calls behind the most recent play (human or computer), for explaining or measuring them.</summary>
    public OffensiveDecision? LastOffense { get; private set; }
    public DefensiveDecision? LastDefense { get; private set; }

    /// <summary>Play the next plate appearance if no human decision is needed.</summary>
    public PlayResult? ExecuteComputerTurn() =>
        State is not { Status: GameStatus.InProgress } || IsHumanTurn || State.PendingInjury is not null
            ? null
            : ExecutePlay();

    // ── Pitching staff ───────────────────────────────────────────────────────

    /// <summary>Tell a reliever to start warming up; he warms as batters are faced.</summary>
    public void StartWarming(bool visitor, int pitcherId)
    {
        var s = InProgress();
        var reliever = s.BullpenFor(visitor).FirstOrDefault(w => w.Player.Id == pitcherId)
            ?? throw new InvalidOperationException("That pitcher is not available in the bullpen.");
        reliever.IsWarming = true;
    }

    /// <summary>Bring in a reliever. The pitcher he replaces is out of the game.</summary>
    public void ChangePitcher(bool visitor, int relieverId) => BringInReliever(visitor, relieverId, putInLineup: true);

    // putInLineup: false only for a double switch, which places the new pitcher in the order itself.
    private void BringInReliever(bool visitor, int relieverId, bool putInLineup)
    {
        var s = InProgress();
        var pen = s.BullpenFor(visitor);
        var incoming = pen.FirstOrDefault(w => w.Player.Id == relieverId)
            ?? throw new InvalidOperationException("That pitcher is not available in the bullpen.");

        var outgoing = s.PitcherFor(visitor);
        var entering = new ActivePitcher
        {
            Player = incoming.Player,
            EntryModifier = FatigueModel.GetWarmupModifier(incoming),
        };
        FatigueModel.GetModifier(entering);  // set the displayed condition

        pen.Remove(incoming);
        s.UsedFor(visitor).Add(outgoing.Player.Id);
        if (visitor) s.VisitorPitcher = entering; else s.HomePitcher = entering;

        BoxScoreRecorder.EntryLead(s, visitor, BoxScoreRecorder.Pitcher(s, visitor, incoming.Player, 0));
        if (putInLineup && !s.DHRule) PutPitcherInLineup(s, visitor, incoming.Player);
        _moments.Add(GameMoment.PitchingChange);
        LogSubstitution(s, visitor,
            $"{incoming.Player.Name} relieves {outgoing.Player.Name} ({incoming.Status.ToLowerInvariant()}).");
    }

    // WHY: without a designated hitter the pitcher is in the batting order, so the new pitcher takes the old one's spot and
    // position. The spot's holder is found by position, not by name, because a pinch hitter may already have taken it
    // (the pitcher is then out of the game and this change is the one that follows). A runner who is the pitcher is
    // replaced on the base too.
    private static void PutPitcherInLineup(GameState s, bool visitor, Player incoming)
    {
        var lineup = s.LineupFor(visitor);
        var positions = s.PositionsFor(visitor);
        int slot = lineup.FindIndex(id => positions[id] == Position.P);
        if (slot < 0) return;

        var holder = s.TeamFor(visitor).Roster.First(p => p.Id == lineup[slot]);
        foreach (int b in new[] { 1, 2, 3 })
        {
            var runner = b switch { 1 => s.RunnerOnFirst, 2 => s.RunnerOnSecond, _ => s.RunnerOnThird };
            if (runner?.Player.Id == holder.Id)
                PutRunner(s, b, new BaseRunner
                {
                    Player = incoming, BattingSlot = runner.BattingSlot,
                    ResponsiblePitcherId = runner.ResponsiblePitcherId,
                });
        }
        Swap(s, visitor, slot, holder, incoming, "");
    }

    /// <summary>
    /// A double switch: the new pitcher bats in the spot of a fielder who leaves, and a bench player takes that fielder's
    /// position and bats in the pitcher's old spot. The pitcher's spot comes up sooner than the fielder's, so the bench
    /// bat gets the plate appearances and the pitcher is hidden at the bottom of the order. Only without a designated hitter.
    /// </summary>
    public void DoubleSwitch(bool visitor, int relieverId, int lineupPlayerId, int benchPlayerId)
    {
        var s = InProgress();
        if (s.DHRule)
            throw new InvalidOperationException("There is no double switch with a designated hitter.");
        var lineup = s.LineupFor(visitor);
        var positions = s.PositionsFor(visitor);
        if (!lineup.Contains(lineupPlayerId))
            throw new InvalidOperationException("That player is not in the lineup.");
        if (positions[lineupPlayerId] == Position.P)
            throw new InvalidOperationException("Choose a fielder to switch, not the pitcher.");
        int pitcherSlot = lineup.FindIndex(id => positions[id] == Position.P);
        if (pitcherSlot < 0)
            throw new InvalidOperationException("The pitcher is not in the batting order.");
        if (IsBusyOffensively(s, visitor, lineupPlayerId) || IsBusyOffensively(s, visitor, lineup[pitcherSlot]))
            throw new InvalidOperationException("The batter or a runner cannot be switched now.");

        var roster = s.TeamFor(visitor).Roster;
        var holder = roster.First(p => p.Id == lineup[pitcherSlot]);
        var leaving = roster.First(p => p.Id == lineupPlayerId);
        var bench = TakeBenchPlayer(s, visitor, benchPlayerId);
        int fielderSlot = lineup.IndexOf(lineupPlayerId);
        var fieldingPosition = positions[lineupPlayerId];

        BringInReliever(visitor, relieverId, putInLineup: false);
        var reliever = s.PitcherFor(visitor).Player;

        positions.Remove(holder.Id);
        positions.Remove(leaving.Id);
        positions[bench.Id] = fieldingPosition;
        positions[reliever.Id] = Position.P;
        lineup[pitcherSlot] = bench.Id;
        lineup[fielderSlot] = reliever.Id;
        s.UsedFor(visitor).Add(holder.Id);
        s.UsedFor(visitor).Add(leaving.Id);
        BoxScoreRecorder.Substituted(s, visitor, bench, "");
        BoxScoreRecorder.Substituted(s, visitor, reliever, "");

        LogSubstitution(s, visitor,
            $"Double switch: {reliever.Name} bats {Ordinal(fielderSlot + 1)}; {bench.Name} replaces {leaving.Name} at " +
            $"{LineupRules.Label(fieldingPosition)}, batting {Ordinal(pitcherSlot + 1)}.");
    }

    private static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th" };

    // WHY: a pinch hitter who batted for the pitcher took his spot (and position P), so the pitcher is out of the game and a
    // reliever has to take over when the side takes the field. The manager who made the move gets to choose, as he does for a
    // computer side; a human's is chosen by the default manager, who prefers a reliever who is already warm.
    private void EnforcePitcherAfterPinchHit()
    {
        var s = State!;
        if (s.DHRule || s.Status != GameStatus.InProgress) return;
        bool visitor = !s.IsTopOfInning;  // the side that fields in this half-inning
        var lineup = s.LineupFor(visitor);
        var positions = s.PositionsFor(visitor);
        int slot = lineup.FindIndex(id => positions[id] == Position.P);
        if (slot < 0 || lineup[slot] == s.PitcherFor(visitor).Player.Id) return;   // the pitcher is still the one in the order

        var manager = ControlOf(s, visitor) == ManagerControl.Computer ? ManagerFor(visitor) : _cpu;
        if (manager.ChooseForcedReliever(s, visitor) is not int relieverId) return;
        ChangePitcher(visitor, relieverId);
    }

    // As a computer-managed side takes the field, it may swap a weak glove for a better one to protect a lead.
    private void ManageComputerDefense()
    {
        var s = State!;
        if (s.Status != GameStatus.InProgress || s.PendingInjury is not null) return;

        bool visitor = !s.IsTopOfInning;  // the side that fields in this half-inning
        if (ControlOf(s, visitor) != ManagerControl.Computer) return;

        if (ManagerFor(visitor).ChooseDefensiveReplacement(s, visitor) is { } move)
            Replace(visitor, move.OutPlayerId, move.InPlayerId);
    }

    // After each play, a computer-managed fielding side may start warming up or change pitchers
    // so the next batter faces the right man (and a human batting against it sees the change).
    private void ManageComputerPitching()
    {
        var s = State!;
        if (s.Status != GameStatus.InProgress || s.PendingInjury is not null) return;

        bool visitor = !s.IsTopOfInning;  // the side that fields next
        if (ControlOf(s, visitor) != ManagerControl.Computer) return;

        var manager = ManagerFor(visitor);
        var move = manager.DecidePitching(s, visitor);
        if (move.StartWarmingId is int warm) StartWarming(visitor, warm);
        if (move.ChangeToId is int relief)
        {
            // With the pitcher in the batting order a change is a chance for a double switch.
            if (!s.DHRule && manager.ChooseDoubleSwitch(s, visitor, relief) is { } ds)
                DoubleSwitch(visitor, relief, ds.LineupPlayerId, ds.BenchPlayerId);
            else
                ChangePitcher(visitor, relief);
        }
    }

    /// <summary>The computer's fielding decision for the next at-bat (when a human bats).</summary>
    /// <param name="plannedBatter">The pinch hitter the human batting manager has announced, if any.</param>
    public DefensiveDecision GetComputerDefense(Player? plannedBatter = null) =>
        ManagerFor(!State!.IsTopOfInning).MakeDefensiveDecision(State!, plannedBatter);

    /// <summary>The persona running a side, for showing the player who is making the computer's calls.</summary>
    public ManagerPersona? PersonaFor(bool visitor) =>
        _catalog?.Get(visitor ? State!.VisitorManagerId : State!.HomeManagerId);

    // ── Substitutions ────────────────────────────────────────────────────────
    // A player who leaves the game cannot return. A replacement takes the batting
    // slot and fielding position of the player he replaces; use SwapPositions to
    // shuffle the defense afterwards. All methods throw InvalidOperationException
    // with a user-presentable message when the move is not allowed.

    /// <summary>Pinch hit for the batter currently at the plate (batting side only).</summary>
    public void PinchHit(int benchPlayerId)
    {
        var s = InProgress();
        bool visitor = s.IsTopOfInning;
        var outgoing = s.CurrentBatter;
        var incoming = TakeBenchPlayer(s, visitor, benchPlayerId);

        // A pinch hitter for the pitcher takes his spot, so the pitcher leaves the game and a reliever must replace him.
        bool forPitcher = !s.DHRule && s.PositionsFor(visitor)[outgoing.Id] == Position.P;
        if (forPitcher && s.BullpenFor(visitor).Count == 0)
            throw new InvalidOperationException("There is nobody left in the bullpen to take over, so the pitcher has to bat.");

        int slot = s.BatterIndexFor(visitor) % s.LineupFor(visitor).Count;
        Swap(s, visitor, slot, outgoing, incoming, "PH");
        LogSubstitution(s, visitor, forPitcher
            ? $"Pinch hitter {incoming.Name} for the pitcher, {outgoing.Name}. A reliever will take the mound."
            : $"Pinch hitter {incoming.Name} for {outgoing.Name}.");
        TidyDefense(visitor);
    }

    /// <summary>Pinch run for the runner on the given base (1–3) of the batting side.</summary>
    public void PinchRun(int baseIndex, int benchPlayerId)
    {
        var s = InProgress();
        bool visitor = s.IsTopOfInning;
        var runner = baseIndex switch
        {
            1 => s.RunnerOnFirst,
            2 => s.RunnerOnSecond,
            3 => s.RunnerOnThird,
            _ => throw new InvalidOperationException("Base must be 1, 2 or 3.")
        } ?? throw new InvalidOperationException("There is no runner on that base.");

        var incoming = TakeBenchPlayer(s, visitor, benchPlayerId);
        int slot = s.LineupFor(visitor).IndexOf(runner.Player.Id);
        if (slot < 0) throw new InvalidOperationException($"{runner.Player.Name} is not in the lineup.");

        var replacement = new BaseRunner
        {
            Player = incoming, BattingSlot = runner.BattingSlot,
            ResponsiblePitcherId = runner.ResponsiblePitcherId,   // the pitcher who let him on is still charged
        };
        Swap(s, visitor, slot, runner.Player, incoming, "PR");
        PutRunner(s, baseIndex, replacement);
        LogSubstitution(s, visitor, $"Pinch runner {incoming.Name} for {runner.Player.Name}.");
        TidyDefense(visitor);
    }

    /// <summary>
    /// Replace a lineup player with a bench player at the same batting slot and position.
    /// The batter at the plate and runners on base are changed with PinchHit / PinchRun instead.
    /// </summary>
    public void Replace(bool visitor, int lineupPlayerId, int benchPlayerId)
    {
        var s = InProgress();
        if (!s.LineupFor(visitor).Contains(lineupPlayerId))
            throw new InvalidOperationException("That player is not in the lineup.");
        if (s.PositionsFor(visitor)[lineupPlayerId] == Position.P)
            throw new InvalidOperationException("Use a pitching change (or a double switch) to replace the pitcher.");
        if (IsBusyOffensively(s, visitor, lineupPlayerId))
            throw new InvalidOperationException(
                "Use pinch hit or pinch run to replace the batter or a runner on base.");

        ReplaceCore(s, visitor, lineupPlayerId, benchPlayerId, "{0} replaces {1} at {2}.");
    }

    /// <summary>Trade the fielding positions of two lineup players (batting order is unchanged).</summary>
    public void SwapPositions(bool visitor, int playerIdA, int playerIdB)
    {
        var s = InProgress();
        var lineup = s.LineupFor(visitor);
        if (playerIdA == playerIdB || !lineup.Contains(playerIdA) || !lineup.Contains(playerIdB))
            throw new InvalidOperationException("Choose two different players from the lineup.");

        var positions = s.PositionsFor(visitor);
        if (positions[playerIdA] == Position.P || positions[playerIdB] == Position.P)
            throw new InvalidOperationException("The pitcher cannot change positions; use a pitching change.");
        (positions[playerIdA], positions[playerIdB]) = (positions[playerIdB], positions[playerIdA]);

        var roster = s.TeamFor(visitor).Roster;
        var a = roster.First(p => p.Id == playerIdA);
        var b = roster.First(p => p.Id == playerIdB);
        BoxScoreRecorder.PositionChanged(s, visitor, a);
        BoxScoreRecorder.PositionChanged(s, visitor, b);
        LogSubstitution(s, visitor,
            $"{a.Name} moves to {LineupRules.Label(positions[a.Id])}, {b.Name} to {LineupRules.Label(positions[b.Id])}.");
    }

    private void ApplyOffensiveSubstitutions(OffensiveDecision offense)
    {
        if (offense.PinchRunnerPlayerId is int runnerId && offense.PinchRunnerBaseIndex is int baseIndex)
            PinchRun(baseIndex, runnerId);
        if (offense.PinchHitterPlayerId is int hitterId)
            PinchHit(hitterId);
    }

    private void ReplaceCore(GameState s, bool visitor, int lineupPlayerId, int benchPlayerId, string logFormat)
    {
        var outgoing = s.TeamFor(visitor).Roster.First(p => p.Id == lineupPlayerId);
        var incoming = TakeBenchPlayer(s, visitor, benchPlayerId);
        Swap(s, visitor, s.LineupFor(visitor).IndexOf(lineupPlayerId), outgoing, incoming, "");
        LogSubstitution(s, visitor, string.Format(logFormat, incoming.Name, outgoing.Name,
            LineupRules.Label(s.PositionsFor(visitor)[incoming.Id])));
        TidyDefense(visitor);
    }

    private GameState InProgress() =>
        State is { Status: GameStatus.InProgress } s
            ? s
            : throw new InvalidOperationException("No game in progress.");

    private static ManagerControl ControlOf(GameState s, bool visitor) =>
        visitor ? s.VisitorControl : s.HomeControl;

    private static Player TakeBenchPlayer(GameState s, bool visitor, int playerId) =>
        s.Bench(visitor).FirstOrDefault(p => p.Id == playerId)
        ?? throw new InvalidOperationException("That player is not available on the bench.");

    private static bool IsBusyOffensively(GameState s, bool visitor, int playerId)
    {
        if (s.IsTopOfInning != visitor) return false;  // not batting right now
        return s.CurrentBatter.Id == playerId
            || s.RunnerOnFirst?.Player.Id == playerId
            || s.RunnerOnSecond?.Player.Id == playerId
            || s.RunnerOnThird?.Player.Id == playerId;
    }

    private static void PutRunner(GameState s, int baseIndex, BaseRunner runner)
    {
        switch (baseIndex)
        {
            case 1: s.RunnerOnFirst = runner; break;
            case 2: s.RunnerOnSecond = runner; break;
            default: s.RunnerOnThird = runner; break;
        }
    }

    private static void Swap(GameState s, bool visitor, int slot, Player outgoing, Player incoming, string role)
    {
        var positions = s.PositionsFor(visitor);
        positions[incoming.Id] = positions[outgoing.Id];
        positions.Remove(outgoing.Id);
        s.LineupFor(visitor)[slot] = incoming.Id;
        s.UsedFor(visitor).Add(outgoing.Id);
        BoxScoreRecorder.Substituted(s, visitor, incoming, role);
    }

    // A computer manager moves players to where they field best after a substitution.
    private void TidyDefense(bool visitor)
    {
        var s = State!;
        if (ControlOf(s, visitor) != ManagerControl.Computer) return;
        foreach (var (a, b) in ManagerFor(visitor).BestPositionSwaps(s, visitor))
            SwapPositions(visitor, a, b);
    }

    private static void LogSubstitution(GameState s, bool visitor, string text)
    {
        var team = s.TeamFor(visitor);
        s.PlayByPlay.Insert(0, new PlayEvent
        {
            Inning = s.CurrentInning,
            IsTop = s.IsTopOfInning,
            Description = $"{team.Abbreviation}: {text}",
            VisitorScore = s.VisitorTotalScore,
            HomeScore = s.HomeTotalScore,
        });
    }

    // The chosen pitcher must be on the team's staff; none chosen means the ace.
    private static Player ResolveStarter(Team team, int? chosenId)
    {
        if (chosenId is null) return team.DefaultStarter;
        return team.Pitchers.FirstOrDefault(p => p.Id == chosenId)
            ?? throw new ArgumentException($"Player {chosenId} is not a pitcher on {team.DisplayName}.");
    }

    private static List<LineupSlot> ResolveLineup(Team team, IReadOnlyList<LineupSlot>? custom,
        bool designatedHitter, Player starter)
    {
        if (custom is null) return LineupRules.Default(team, designatedHitter, starter);

        var errors = LineupRules.Validate(team, custom, designatedHitter, starter.Id);
        if (errors.Count > 0)
            throw new ArgumentException($"Invalid {team.DisplayName} lineup: {string.Join(" ", errors)}");
        return [.. custom];
    }

    // ── Injuries, ejections and weather ──────────────────────────────────────

    /// <summary>
    /// Replace the hurt player now that he has left the game: a bench player for a hitter or fielder,
    /// or a reliever for the pitcher. Only needed for a human-managed side; the computer chooses its own.
    /// </summary>
    public void ResolveInjury(int replacementId)
    {
        var s = InProgress();
        var notice = s.PendingInjury
            ?? throw new InvalidOperationException("Nobody is waiting to be replaced.");

        if (notice.IsPitcher)
            ChangePitcher(notice.Visitor, replacementId);
        else
            ForceReplace(s, notice.Visitor, notice.PlayerId, replacementId);

        s.PendingInjury = null;
    }

    // Like Replace, but also for the man at bat or on base: an injured player can't wait for a pinch move.
    private void ForceReplace(GameState s, bool visitor, int injuredId, int replacementId)
    {
        var outgoing = s.TeamFor(visitor).Roster.First(p => p.Id == injuredId);
        var incoming = TakeBenchPlayer(s, visitor, replacementId);
        int slot = s.LineupFor(visitor).IndexOf(injuredId);

        foreach (int b in new[] { 1, 2, 3 })
        {
            var runner = b switch { 1 => s.RunnerOnFirst, 2 => s.RunnerOnSecond, _ => s.RunnerOnThird };
            if (runner?.Player.Id == injuredId)
                PutRunner(s, b, new BaseRunner
                {
                    Player = incoming, BattingSlot = runner.BattingSlot,
                    ResponsiblePitcherId = runner.ResponsiblePitcherId,
                });
        }

        Swap(s, visitor, slot, outgoing, incoming, "");
        LogSubstitution(s, visitor,
            $"{incoming.Name} replaces the injured {outgoing.Name} at {LineupRules.Label(s.PositionsFor(visitor)[incoming.Id])}.");
        TidyDefense(visitor);
    }

    // Where the hurt player was, for showing it.
    private static GameEvent InjuryEvent(GameState s, bool visitor, Player hurt, bool isPitcher, Player batter)
    {
        if (isPitcher) return new GameEvent(GameEventKind.Injury, visitor, hurt.Name, InjuryRole.Pitcher);
        if (hurt.Id == batter.Id) return new GameEvent(GameEventKind.Injury, visitor, hurt.Name, InjuryRole.Batter);
        var runners = new[] { s.RunnerOnFirst, s.RunnerOnSecond, s.RunnerOnThird };
        for (int i = 0; i < runners.Length; i++)
            if (runners[i]?.Player.Id == hurt.Id)
                return new GameEvent(GameEventKind.Injury, visitor, hurt.Name, InjuryRole.Runner, Base: i + 1);
        var position = s.PositionsFor(visitor).TryGetValue(hurt.Id, out var pos) ? pos : (Position?)null;
        return new GameEvent(GameEventKind.Injury, visitor, hurt.Name, InjuryRole.Fielder, position);
    }

    internal void MaybeInjurePlayer(PlayResult result, bool wasTop, Player batter)
    {
        var s = State!;
        if (!s.Options.Injuries || s.PendingInjury is not null) return;
        if (_events.NextDouble() >= RandomEvents.InjuryPerPlay) return;

        // Who could have been hurt: the batter, runners, the pitcher (more likely when tired), a fielder.
        var pool = new List<(bool Visitor, Player Player, bool IsPitcher, double Weight)>();
        void Add(bool visitor, Player p, bool isPitcher, double weight)
        {
            if (pool.Any(x => x.Player.Id == p.Id)) return;
            // A pitcher who is batting or running is replaced by a reliever, not a bench player.
            isPitcher |= p.IsPitcher;
            bool canReplace = isPitcher ? s.BullpenFor(visitor).Count > 0 : s.Bench(visitor).Any();
            if (canReplace) pool.Add((visitor, p, isPitcher, weight));
        }

        Add(wasTop, batter, false, 1.0);
        foreach (var r in new[] { s.RunnerOnFirst, s.RunnerOnSecond, s.RunnerOnThird })
            if (r is not null && s.LineupFor(wasTop).Contains(r.Player.Id)) Add(wasTop, r.Player, false, 1.0);

        var pitcher = s.PitcherFor(!wasTop);
        Add(!wasTop, pitcher.Player, true, 1.0 + pitcher.PitchCount / 60.0);
        foreach (var (p, pos) in s.LineupPlayers(!wasTop).Where(x => x.Position is not (Position.DH or Position.P)))
            Add(!wasTop, p, false, 0.35);

        if (pool.Count == 0) return;

        double roll = _events.NextDouble() * pool.Sum(x => x.Weight);
        var hurt = pool[^1];
        foreach (var candidate in pool)
        {
            if ((roll -= candidate.Weight) < 0) { hurt = candidate; break; }
        }

        s.PendingInjury = new InjuryNotice(hurt.Visitor, hurt.Player.Id, hurt.IsPitcher);
        _happenings.Add(InjuryEvent(s, hurt.Visitor, hurt.Player, hurt.IsPitcher, batter));
        string text = $"{hurt.Player.Name} is injured and must leave the game.";
        LogSubstitution(s, hurt.Visitor, text);
        BoxScoreRecorder.Note(s, $"{s.TeamFor(hurt.Visitor).Abbreviation}: {text}");

        if (ControlOf(s, hurt.Visitor) == ManagerControl.Computer)
            ResolveInjury(ManagerFor(hurt.Visitor).ChooseInjuryReplacement(s, s.PendingInjury));
    }

    internal void MaybeEjectManager(PlayResult result, bool wasTop)
    {
        var s = State!;
        if (!s.Options.Ejections) return;
        if (result.Outcome is not (OutcomeType.Strikeout or OutcomeType.CaughtStealing or OutcomeType.Walk)) return;
        if (_events.NextDouble() >= RandomEvents.EjectionPerDisputedPlay) return;

        // Strikeouts and caught stealing are argued by the batting side, walks by the fielding side.
        bool visitor = result.Outcome == OutcomeType.Walk ? !wasTop : wasTop;
        if (visitor ? s.VisitorManagerEjected : s.HomeManagerEjected) return;

        if (visitor) s.VisitorManagerEjected = true; else s.HomeManagerEjected = true;
        bool wasHuman = ControlOf(s, visitor) == ManagerControl.Human;
        if (wasHuman)
        {
            // The computer takes over the dugout for the rest of the game.
            if (visitor) s.VisitorControl = ManagerControl.Computer; else s.HomeControl = ManagerControl.Computer;
        }

        string team = s.TeamFor(visitor).Abbreviation;
        LogSubstitution(s, visitor, wasHuman
            ? "The manager is ejected for arguing with the umpire. The computer takes over the team."
            : "The manager is ejected for arguing with the umpire.");
        BoxScoreRecorder.Note(s, $"{team}: manager ejected.");
        _moments.Add(GameMoment.ManagerEjected);
        _happenings.Add(new GameEvent(GameEventKind.Ejection, visitor));
    }

    // A rain delay can come at the start of any half-inning after the first.
    internal void MaybeRainDelay()
    {
        var s = State!;
        if (!s.Options.RainDelays || s.Status != GameStatus.InProgress || s.CurrentInning < 2) return;
        if (_events.NextDouble() >= RandomEvents.RainPerHalfInning) return;

        s.RainDelays++;
        int minutes = 20 + _events.Next(0, 80);
        _happenings.Add(new GameEvent(GameEventKind.RainDelay, Visitor: true, Minutes: minutes));

        // Official after five innings (4½ if the home team is ahead); a tie is never called.
        double complete = (s.CurrentInning - 1) + (s.IsTopOfInning ? 0 : 0.5);
        bool official = complete >= 5 || (complete >= 4.5 && s.HomeTotalScore > s.VisitorTotalScore);
        if (official && s.VisitorTotalScore != s.HomeTotalScore && _events.NextDouble() < RandomEvents.RainCallChance)
        {
            s.EndedByRain = true;
            string called = $"Rain delay of {minutes} minutes. The game is called.";
            LogSubstitution(s, s.IsTopOfInning, called);
            BoxScoreRecorder.Note(s, $"Game called by rain after {complete:0.#} innings.");
            EndGame();
            return;
        }

        // Play resumes, but arms have cooled: warm-ups are lost and the pitchers have to settle in again.
        foreach (bool visitor in new[] { true, false })
        {
            foreach (var w in s.BullpenFor(visitor)) { w.IsWarming = false; w.BattersFacedWarming = 0; }
            var p = s.PitcherFor(visitor);
            p.EntryModifier = RandomEvents.PostDelayModifier;
            p.SettleFrom = p.BattersFaced;
        }
        string text = $"Rain delay of {minutes} minutes. Pitchers have cooled off and the bullpens must warm up again.";
        LogSubstitution(s, s.IsTopOfInning, text);
        BoxScoreRecorder.Note(s, $"Rain delay ({minutes} min) in the {(s.IsTopOfInning ? "top" : "bottom")} of the {s.CurrentInning}.");
    }

    // ── Game loop internals ──────────────────────────────────────────────────

    private void EndGame()
    {
        var s = State!;
        s.Status = GameStatus.Final;
        s.PendingInjury = null;
        BoxScoreRecorder.Finish(s);
    }

    // internal so flow rules (innings, walk-offs) can be tested with exact results
    internal void ApplyResult(PlayResult result)
    {
        var s = State!;
        int marginBefore = s.VisitorTotalScore - s.HomeTotalScore;

        // Credit the batter, runners and pitchers while the bases still show who was where.
        BoxScoreRecorder.RecordPlay(s, result);

        // Runners who reach base are charged to the pitcher who let them on.
        int pitcherId = s.CurrentPitcher.Player.Id;
        foreach (var runner in new[] { result.NewFirst, result.NewSecond, result.NewThird })
            if (runner is not null && runner.ResponsiblePitcherId == 0)
                runner.ResponsiblePitcherId = pitcherId;

        // Update base state
        s.RunnerOnFirst  = result.NewFirst;
        s.RunnerOnSecond = result.NewSecond;
        s.RunnerOnThird  = result.NewThird;

        // Score runs
        if (result.RunsScored > 0)
            s.AddRuns(s.IsTopOfInning, s.CurrentInning, result.RunsScored);
        BoxScoreRecorder.TrackLead(s, marginBefore);
        if (result.Outcome == OutcomeType.Error) s.ErrorThisHalfInning = true;
        if (result.RunsScored > 0)
        {
            int inningRuns = s.RunsInInning(s.IsTopOfInning, s.CurrentInning);
            if (inningRuns >= 3 && inningRuns - result.RunsScored < 3) _moments.Add(GameMoment.Rally);
        }

        // Record outs
        s.Outs += result.OutsRecorded;

        // A steal attempt is not a plate appearance: the batter stays up and nobody is "faced".
        bool plateAppearance = result.Outcome is not (OutcomeType.StolenBase or OutcomeType.CaughtStealing);

        var activePitcher = s.CurrentPitcher;
        activePitcher.PitchCount += Math.Max(result.PitchesThrown, 1);
        activePitcher.RunsAllowed += result.RunsScored;
        if (plateAppearance)
        {
            activePitcher.BattersFaced++;
            WarmBullpen(s.BullpenFor(!s.IsTopOfInning));

            if (s.IsTopOfInning)
                s.VisitorBatterIndex = (s.VisitorBatterIndex + 1) % s.VisitorLineup.Count;
            else
                s.HomeBatterIndex = (s.HomeBatterIndex + 1) % s.HomeLineup.Count;
        }

        // Walk-off: the home team takes the lead in the bottom of the 9th or later.
        if (!s.IsTopOfInning && s.CurrentInning >= 9 && s.HomeTotalScore > s.VisitorTotalScore)
        {
            EndGame();
            return;
        }

        if (s.Outs >= 3)
            AdvanceHalfInning();
    }

    // Relievers told to warm up get one batter closer to ready for each batter their team's pitcher faces.
    private static void WarmBullpen(List<WarmingPitcher> bullpen)
    {
        foreach (var w in bullpen.Where(w => w.IsWarming))
            w.BattersFacedWarming++;
    }

    private void AdvanceHalfInning()
    {
        var s = State!;
        BoxScoreRecorder.HalfInningEnded(s);
        s.Outs = 0;
        s.RunnerOnFirst = s.RunnerOnSecond = s.RunnerOnThird = null;
        s.ErrorThisHalfInning = false;

        if (s.IsTopOfInning)
        {
            // The home team wins without batting if it leads after the top of the 9th or later.
            if (s.CurrentInning >= 9 && s.HomeTotalScore > s.VisitorTotalScore)
            {
                EndGame();
                return;
            }
            s.IsTopOfInning = false;
            if (s.CurrentInning == 7) _moments.Add(GameMoment.SeventhInningStretch);
            MaybeRainDelay();
            return;
        }

        // End of the bottom half: anything but a tie ends the game from the 9th on.
        if (s.CurrentInning >= 9 && s.VisitorTotalScore != s.HomeTotalScore)
        {
            EndGame();
            return;
        }

        s.CurrentInning++;
        s.IsTopOfInning = true;
        MaybeRainDelay();
    }

    private void LogPlay(PlayResult result, int inning, bool isTop)
    {
        var s = State!;
        s.PlayByPlay.Insert(0, new PlayEvent
        {
            Inning = inning,
            IsTop = isTop,
            Description = result.Description,
            VisitorScore = s.VisitorTotalScore,
            HomeScore = s.HomeTotalScore,
        });
    }
}
