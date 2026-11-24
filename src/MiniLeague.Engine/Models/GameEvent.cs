namespace MiniLeague.Engine.Models;

public enum GameEventKind { Injury, Ejection, RainDelay }

/// <summary>Who an injured player was, for showing it: where he was when he was hurt.</summary>
public enum InjuryRole { Pitcher, Fielder, Runner, Batter }

/// <summary>
/// Something that interrupted the game, kept so it can be shown (see GameService.TakeEvents). Display only:
/// the game itself has already dealt with it, and nothing here is saved with a game.
/// </summary>
/// <param name="Visitor">The team it happened to (for a rain delay, the visitors by convention).</param>
/// <param name="Role">For an injury: where the player was.</param>
/// <param name="Position">For an injury to a fielder: his position.</param>
/// <param name="Base">For an injury to a runner: the base he was on (1 to 3).</param>
/// <param name="Minutes">For a rain delay: how long it lasted.</param>
public record GameEvent(
    GameEventKind Kind, bool Visitor, string PlayerName = "",
    InjuryRole Role = InjuryRole.Fielder, Position? Position = null, int Base = 0, int Minutes = 0);
