namespace MiniLeague.Engine.Models;

public class Stadium
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;

    // Dimensions in feet
    public int LeftField { get; set; } = 330;
    public int LeftCenter { get; set; } = 375;
    public int Center { get; set; } = 400;
    public int RightCenter { get; set; } = 375;
    public int RightField { get; set; } = 330;
    public int LeftWallHeight { get; set; } = 8;
    public int RightWallHeight { get; set; } = 8;

    // Park factors (1.0 = neutral)
    public double HomeRunFactor { get; set; } = 1.0;
    public double HomeRunFactorLeft { get; set; } = 1.0;   // vs left-handed batters
    public double HomeRunFactorRight { get; set; } = 1.0;  // vs right-handed batters
    public double DoublesTriplesFactor { get; set; } = 1.0;
    public double RunsFactor { get; set; } = 1.0;

    public double SinglesFactor { get; set; } = 1.0;       // artificial turf plays faster

    public bool IsDome { get; set; }
    public bool IsTurf { get; set; }

    public static Stadium Generic => new()
    {
        Id = "generic",
        Name = "Generic Stadium",
        City = string.Empty
    };
}
