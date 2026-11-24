namespace MiniLeague.Engine.Animation;

/// <summary>Collects actors, keys and cues, keeps each track in time order, and produces the finished script.</summary>
internal sealed class ScriptBuilder
{
    private readonly List<Actor> _actors = [];
    private readonly Dictionary<string, List<Key>> _keys = [];
    private readonly List<Cue> _cues = [];

    private static double R(double v) => Math.Round(v, 3);

    public void AddActor(Actor actor, FieldPoint at, string pose, double z = 0)
    {
        _actors.Add(actor);
        _keys[actor.Id] = [new Key(0, R(at.X), R(at.Y), R(z), pose)];
    }

    public FieldPoint PointOf(string id) => new(_keys[id][^1].X, _keys[id][^1].Y);

    public double TimeOf(string id) => _keys[id][^1].T;

    /// <summary>When the last thing anybody does happens so far.</summary>
    public double LastTime => _keys.Values.Max(k => k[^1].T);

    /// <summary>Adds a key. Times may repeat but never go backwards on a track.</summary>
    public void Move(string id, double t, FieldPoint at, double z = 0, string? pose = null,
                     Ease ease = Ease.Linear, double apex = 0, string? facing = null)
    {
        var keys = _keys[id];
        if (t < keys[^1].T - 1e-9)
            throw new InvalidOperationException($"{id} cannot move at {t:0.###}s: it is already at {keys[^1].T:0.###}s.");
        keys.Add(new Key(R(Math.Max(t, keys[^1].T)), R(at.X), R(at.Y), R(z), pose, ease, R(apex), facing));
    }

    /// <summary>Changes the pose on an actor's last key (how he ends up: standing, out, safe).</summary>
    public void EndPose(string id, string pose)
    {
        var keys = _keys[id];
        keys[^1] = keys[^1] with { Pose = pose };
    }

    public void Cue(double t, string kind, string value, FieldPoint? at = null) =>
        _cues.Add(new Cue(R(t), kind, value, at is { } p ? R(p.X) : null, at is { } q ? R(q.Y) : null));

    public PlayScript Build(string outcome, double settle)
    {
        double end = 0;
        foreach (var keys in _keys.Values) end = Math.Max(end, keys[^1].T);
        foreach (var cue in _cues) end = Math.Max(end, cue.T);

        return new PlayScript(
            PlayScript.CurrentVersion,
            outcome,
            R(end + settle),
            _actors,
            _actors.Select(a => new Track(a.Id, _keys[a.Id])).ToList(),
            _cues.OrderBy(c => c.T).ToList());
    }
}
