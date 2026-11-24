namespace MiniLeague.Engine.AI;

/// <summary>One line of a manager's profile: a label and a rating from 1 (little) to 5 (a great deal).</summary>
public sealed record ManagerTrait(string Label, int Rating);

/// <summary>
/// A computer manager as the player meets him: a made-up person with a nickname, a style of play and a profile,
/// together with the way to build the manager who makes those decisions. All names here are fictional.
/// </summary>
/// <param name="Id">Stable key saved with a game, so a resumed game keeps the same manager. Never change it once released.</param>
/// <param name="Name">The fictional manager's name, e.g. "Ellie Whitcombe".</param>
/// <param name="Nickname">What the clubhouse calls the manager, e.g. "The Actuary".</param>
/// <param name="Style">The management approach in a few words, e.g. "By the numbers".</param>
/// <param name="Tagline">One sentence a player can read to choose.</param>
/// <param name="Biography">A short fictional background that explains the style.</param>
/// <param name="Traits">The profile: how much the manager leans on each part of the game.</param>
/// <param name="Create">Builds the manager; the argument is the random source the manager should use.</param>
public sealed record ManagerPersona(
    string Id,
    string Name,
    string Nickname,
    string Style,
    string Tagline,
    string Biography,
    IReadOnlyList<ManagerTrait> Traits,
    Func<Random, ComputerManager> Create)
{
    /// <summary>"Ellie Whitcombe, The Actuary", as shown in a menu.</summary>
    public string DisplayName => $"{Name}, {Nickname}";
}
