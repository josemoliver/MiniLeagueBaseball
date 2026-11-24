using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Animation;

// A play script is plain data describing how to show one play: who moves where and when, plus cues for
// sounds and effects. It uses ground-plane feet (see FieldGeometry) and carries no screen coordinates.
// It is display only: it never feeds back into the game.

public enum ActorKind { Pitcher, Catcher, Batter, Runner, Fielder, Ball, Teammate }

/// <summary>How an actor gets to a key from the one before it.</summary>
public enum Ease
{
    /// <summary>Straight line at constant speed.</summary>
    Linear,
    /// <summary>Ground position moves straight; height follows a parabola whose peak is the key's Apex.</summary>
    Arc,
    /// <summary>A ball rolling along the ground, slowing as it goes.</summary>
    Roll,
}

/// <summary>Pose names are the interface between the engine and whatever draws the sprites.</summary>
public static class Poses
{
    public const string Stand = "stand";
    public const string Windup = "windup";
    public const string Throw = "throw";
    public const string Swing = "swing";
    public const string Bunt = "bunt";     // squared around, the bat held level out over the plate
    public const string Run = "run";
    public const string Slide = "slide";
    public const string Catch = "catch";
    public const string Dive = "dive";
    public const string Tag = "tag";
    public const string Safe = "safe";
    public const string Out = "out";
    public const string Hit = "hit";
    public const string Cheer = "cheer";   // both arms up
    public const string Kick = "kick";     // the pitcher's leg lifted, hands together
    public const string Sidearm = "sidearm"; // the arm out to the side
    public const string Down = "down";     // lying on the ground
    public const string Crouch = "crouch"; // bent low with the glove at the grass: a man fielding a ground ball
    public const string Transfer = "transfer"; // the ball is coming out of the glove into the throwing hand
    public const string Turn = "turn"; // a cutoff fielder turns toward the next base with the ball secured
    public const string Plant = "plant"; // feet set and throwing arm loaded before the release
    public const string Pop = "pop"; // the catcher rises from his crouch to make a quick throw
    public const string MaskOff = "maskoff"; // the catcher reaches up to shed his mask under a pop-up
    public const string Track = "track"; // a fielder checks his steps while looking up for a pop-up
    public const string Watch = "watch"; // a hitter watches a deep drive after releasing the bat
    public const string Reach = "reach"; // a first baseman anchors a foot and stretches his glove toward a throw
    public const string Recover = "recover"; // a fielder regains his balance after a misplay
    public const string Stretch = "stretch"; // the pitcher's set position, with runners on: hands together, no big wind-up
    public const string Slump = "slump";   // head hanging: a pitcher who has given up a home run, a batter who has struck out
}

public static class CueKind
{
    public const string Sound = "sound";    // Value: a sound name the app already knows (hit, homerun, cheer, ...)
    public const string Banner = "banner";  // Value: the line for the message box
    public const string Effect = "effect";  // Value: a visual effect name, optionally positioned at X and Y
    public const string Card = "card";      // Value: the lines of a score card, separated by newlines
    public const string Hold = "hold";      // Value: seconds to hold this script moment at every playback speed
}

public static class Hands
{
    public const string Left = "left";
    public const string Right = "right";

    public static string Of(ThrowHand hand) => hand == ThrowHand.Left ? Left : Right;

    /// <summary>The side a hitter stands on (a switch hitter has already chosen one by the time he is at the plate).</summary>
    public static string Of(BatHand hand) => hand == BatHand.Left ? Left : Right;
}

public static class TeamSide
{
    public const string Home = "home";
    public const string Visitor = "visitor";
}

/// <summary>Someone or something on the field. Team is "home" or "visitor" (null for the ball).</summary>
/// <param name="Hand">A defender's throwing arm, or the side a batter stands on ("left" or "right").</param>
/// <param name="Stance">How a batter stands (see <see cref="Stances"/>).</param>
public record Actor(string Id, ActorKind Kind, string? Label, string? Team, string? Hand = null, string? Stance = null);

/// <summary>
/// A position at a time. X is toward right field, Y toward center field, Z is height above the ground (feet).
/// Ease, Apex and Facing describe the move *into* this key from the previous one. Pose null keeps the previous pose.
/// </summary>
public record Key(
    double T, double X, double Y, double Z,
    string? Pose = null, Ease Ease = Ease.Linear, double Apex = 0, string? Facing = null);

public record Track(string ActorId, IReadOnlyList<Key> Keys);

public record Cue(double T, string Kind, string Value, double? X = null, double? Y = null);

public record PlayScript(
    int Version,
    string Outcome,
    double Duration,
    IReadOnlyList<Actor> Actors,
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<Cue> Cues)
{
    public const int CurrentVersion = 1;
}
