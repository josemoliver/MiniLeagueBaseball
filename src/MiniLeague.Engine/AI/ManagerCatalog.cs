namespace MiniLeague.Engine.AI;

/// <summary>
/// The computer managers a player can choose from. To add one, add a <see cref="ManagerPersona"/> to
/// <see cref="BuiltIn"/> (or call <see cref="Register"/>): a new tuning of <see cref="ComputerManager"/> needs no
/// other code, and a manager with different logic subclasses it. Nothing else has to change; the setup screen lists
/// whatever the catalog holds.
/// </summary>
public sealed class ManagerCatalog
{
    /// <summary>The manager a game uses when none has been chosen (and for games saved before there was a choice).</summary>
    public const string DefaultId = "analyst";

    private readonly List<ManagerPersona> _personas;

    public ManagerCatalog(IEnumerable<ManagerPersona> personas) => _personas = [.. personas];

    public IReadOnlyList<ManagerPersona> All => _personas;

    /// <summary>The persona with this id, or null when there is none (an id saved by a newer version, say).</summary>
    public ManagerPersona? Find(string? id) =>
        id is null ? null : _personas.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The persona with this id, else the default one, so a stale id never stops a game.</summary>
    public ManagerPersona Get(string? id) => Find(id) ?? Find(DefaultId) ?? _personas[0];

    /// <summary>Builds the manager for this id (the default when unknown).</summary>
    public ComputerManager Create(string? id, Random? rng = null) => Get(id).Create(rng ?? Random.Shared);

    /// <summary>Adds a persona. Throws if its id is already taken, since a saved game could not tell them apart.</summary>
    public void Register(ManagerPersona persona)
    {
        if (Find(persona.Id) is not null)
            throw new ArgumentException($"A manager with the id '{persona.Id}' is already in the catalog.", nameof(persona));
        _personas.Add(persona);
    }

    /// <summary>
    /// The managers that ship with the game. Order is the order on the setup screen.
    /// WHY: personas are built from <see cref="ManagerTuning"/> where they can be, so each one is a few lines and the
    /// differences between them are exactly the dials listed here, nothing hidden in code.
    /// </summary>
    public static IReadOnlyList<ManagerPersona> BuiltIn { get; } =
    [
        new ManagerPersona(
            Id: "analyst",
            Name: "Ellie Whitcombe",
            Nickname: "The Actuary",
            Style: "By the numbers",
            Tagline: "Prices every call in expected runs and only makes the ones that clearly pay.",
            Biography: "A former insurance actuary who took over a last-place club with a spreadsheet and a stopwatch. "
                + "She will not bunt, steal or walk anyone unless the arithmetic says so, and the arithmetic usually says no.",
            Traits:
            [
                new("Aggression on the bases", 3), new("Small ball", 3), new("Bench use", 3),
                new("Quick with the bullpen", 3), new("Intentional walks", 2), new("Late defensive subs", 1),
            ],
            Create: rng => new ComputerManager(rng)),

        new ManagerPersona(
            Id: "oldschool",
            Name: "Gus Halloran",
            Nickname: "Ironhide",
            Style: "The book",
            Tagline: "Plays it the way it has always been played: set rules, no lookahead, a bunt when the book says bunt.",
            Biography: "Thirty-one years in the game, nineteen of them in the dugout, and he has never read a spreadsheet. "
                + "He walks the big bat with first base open, bunts with the weak hitter late, and sends a fast runner on a hunch.",
            Traits:
            [
                new("Aggression on the bases", 3), new("Small ball", 2), new("Bench use", 3),
                new("Quick with the bullpen", 3), new("Intentional walks", 5), new("Late defensive subs", 3),
            ],
            Create: rng => new OldSchoolManager(rng)),

        new ManagerPersona(
            Id: "smallball",
            Name: "Rosa Delgado",
            Nickname: "Spark",
            Style: "One run at a time",
            Tagline: "Bunts, steals and hit-and-runs to manufacture runs, and sends in the fast pinch runner early.",
            Biography: "A scrappy utility infielder who never hit over .240 and made a career of moving runners. "
                + "She manages the way she played: bother the pitcher, take the extra base, and play for the one run.",
            Traits:
            [
                new("Aggression on the bases", 5), new("Small ball", 5), new("Bench use", 4),
                new("Quick with the bullpen", 3), new("Intentional walks", 2), new("Late defensive subs", 4),
            ],
            Create: rng => new ComputerManager(rng, new ManagerTuning
            {
                StealMargin = -0.015, HitAndRunMargin = -0.010, BuntMargin = -0.015, AggressiveRunMargin = -0.005,
                SqueezeMargin = -0.01,
                PinchRunGain = 0.02, PitchoutShare = 0.45,
                // Presses when behind, keeps the hitters guessing, and puts a glove in to protect a lead from the eighth.
                SituationalRisk = 0.5, LeverageSensitivity = 0.3, PitchMix = 0.02, DefensiveReplaceFromInning = 8,
            })),

        new ManagerPersona(
            Id: "slugger",
            Name: "Duke Pruitt",
            Nickname: "Big Sky",
            Style: "Let them swing",
            Tagline: "Never gives up an out on purpose, trusts his starter and lets his hitters hit.",
            Biography: "A power-hitting first baseman who watched managers waste his at-bats on bunts. "
                + "He keeps the bench for emergencies, leaves a good starter in, and would rather lose swinging than win bunting.",
            Traits:
            [
                new("Aggression on the bases", 2), new("Small ball", 1), new("Bench use", 1),
                new("Quick with the bullpen", 1), new("Intentional walks", 1), new("Late defensive subs", 1),
            ],
            Create: rng => new ComputerManager(rng, new ManagerTuning
            {
                StealMargin = ManagerTuning.Never, HitAndRunMargin = ManagerTuning.Never, BuntMargin = ManagerTuning.Never,
                SqueezeMargin = ManagerTuning.Never,
                AggressiveRunMargin = 0.012, PinchHitFromInning = 7, PinchHitGain = 0.05, BenchReserveScale = 1.6,
                PinchRunGain = 0.10, IntentionalWalkMargin = 0.10, ReliefEdgeClose = 0.07, ReliefEdgeOther = 0.14,
                WarmUpPitchCount = 79, WarmUpFromInning = 8, PitchoutShare = 0.15,
                TiringSwapMargin = 0.06, ShelledRuns = 7, DoubleSwitchGain = ManagerTuning.Never,
                // Stubborn: the moment does not change his plan, and he trusts his hitters over the platoon book.
                LeverageSensitivity = -0.3, PlatoonWeight = 0.3,
            })),

        new ManagerPersona(
            Id: "tinkerer",
            Name: "Marcus Delacroix",
            Nickname: "The Tinkerer",
            Style: "Matchups, always",
            Tagline: "Changes pitchers and pinch hitters at the first sign of a better matchup and walks hitters to get one.",
            Biography: "A pitching coach turned manager who keeps a binder on every hitter in the league. "
                + "His bullpen door never stops swinging, his bench plays every night, and he will walk a star to face a lesser bat.",
            Traits:
            [
                new("Aggression on the bases", 3), new("Small ball", 2), new("Bench use", 5),
                new("Quick with the bullpen", 5), new("Intentional walks", 4), new("Late defensive subs", 4),
            ],
            Create: rng => new ComputerManager(rng, new ManagerTuning
            {
                PinchHitFromInning = 5, PinchHitGain = 0.012, BenchReserveScale = 0.5,
                IntentionalWalkMargin = 0.0, IntentionalWalkFromInning = 6, IntentionalWalkOpsGap = 0.08,
                ReliefEdgeClose = -0.02, ReliefEdgeOther = 0.0, WarmUpPitchCount = 39, WarmUpFromInning = 4,
                QuickHookPitchCount = 52, TiringSwapMargin = -0.20, ShelledRuns = 4,
                // The platoon book matters most to him, and the tight spots matter most: a pitch limit, the closer from the
                // eighth, a glove for a lead from the seventh, and some variety in what he calls.
                PlatoonWeight = 2.0, LeverageSensitivity = 0.6, CloserFromInning = 8, StarterPitchLimit = 88,
                DoubleSwitchGain = 0.0,
                DefensiveReplaceFromInning = 7, PitchMix = 0.015,
            })),
    ];

    // NOTE: static members initialize in textual order, so this must stay below BuiltIn or it would be built
    // from a null list.
    /// <summary>The catalog the app uses.</summary>
    public static ManagerCatalog Default { get; } = new(BuiltIn);
}
