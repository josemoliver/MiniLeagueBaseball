#!/usr/bin/env python3
"""
Exports the real-season reference numbers that the simulation is calibrated against.

For each of the 15 teams in the game it writes the team's real season totals (Teams, Batting, Pitching, catchers'
Fielding), the manager of record, and the totals of the team's league that year, to
tools/MiniLeague.ManagerBench/lahman-reference.json. That file is committed, so the calibration runs offline and
need no database; run this script only to regenerate it.

Source: the Lahman Baseball Database (CC BY-SA 3.0), read from a SQL Server database named Lahman.
The connection is taken from the environment, never from a file:

    LAHMAN_SQL_SERVER    host or host\\instance (default 192.168.1.210)
    LAHMAN_SQL_USER      login (default sa)
    LAHMAN_SQL_PASSWORD  password (required)

Needs the `sqlcmd` command-line tool (no Python packages). It only reads.

    python tools/export_lahman_reference.py
"""
import json
import os
import subprocess
import sys
from datetime import date
from pathlib import Path

# WHY: the file names in wwwroot/data/teams do not use Lahman's team codes (the Yankees are NYA there, the Dodgers LAN),
# so the mapping is written out once here and checked against the database below.
TEAMS = [
    ("1927-nyy", 1927, "NYA"), ("1955-bro", 1955, "BRO"), ("1961-nyy", 1961, "NYA"), ("1963-lad", 1963, "LAN"),
    ("1967-stl", 1967, "SLN"), ("1968-det", 1968, "DET"), ("1969-nym", 1969, "NYN"), ("1970-bal", 1970, "BAL"),
    ("1973-oak", 1973, "OAK"), ("1975-bos", 1975, "BOS"), ("1975-cin", 1975, "CIN"), ("1979-pit", 1979, "PIT"),
    ("1980-kc", 1980, "KCA"), ("1980-phi", 1980, "PHI"), ("1982-mil", 1982, "ML4"),
]

OUT = Path(__file__).resolve().parent / "MiniLeague.ManagerBench" / "lahman-reference.json"

TEAM_COLS = ["lgID", "G", "W", "L", "R", "RA", "AB", "H", "2B", "3B", "HR", "BB", "SO", "SB", "CS", "HBP", "SF",
             "ER", "CG", "SHO", "SV", "IPouts", "HA", "HRA", "BBA", "SOA", "E", "DP", "BPF", "PPF", "name", "park"]
BAT_SUMS = ["AB", "H", "2B", "3B", "HR", "BB", "SO", "IBB", "HBP", "SH", "SF", "GIDP", "SB", "CS"]
PIT_SUMS = ["G", "GS", "CG", "SHO", "SV", "IPouts", "BFP", "H", "ER", "HR", "BB", "SO", "IBB", "HBP", "WP", "BK"]


def sql(query: str) -> list[list[str | None]]:
    """Runs one read-only query and returns its rows (NULL as None)."""
    password = os.environ.get("LAHMAN_SQL_PASSWORD")
    if not password:
        sys.exit("Set LAHMAN_SQL_PASSWORD (and optionally LAHMAN_SQL_SERVER, LAHMAN_SQL_USER).")
    env = dict(os.environ, SQLCMDPASSWORD=password)
    # WHY: -C trusts the server certificate (a LAN server with a self-signed one); -W trims padding; -h -1 drops headers.
    proc = subprocess.run(
        ["sqlcmd", "-S", os.environ.get("LAHMAN_SQL_SERVER", "192.168.1.210"), "-U", os.environ.get("LAHMAN_SQL_USER", "sa"),
         "-C", "-d", "Lahman", "-h", "-1", "-W", "-s", "|", "-b", "-Q", "SET NOCOUNT ON; " + query],
        capture_output=True, text=True, env=env)
    if proc.returncode != 0:
        sys.exit(f"sqlcmd failed: {proc.stdout.strip() or proc.stderr.strip()}")
    return [[None if c == "NULL" else c for c in line.split("|")] for line in proc.stdout.splitlines() if line.strip()]


def num(v: str | None):
    if v is None:
        return None
    f = float(v)
    return int(f) if f == int(f) else f


def cols(names: list[str]) -> str:
    return ", ".join(f"[{n}]" for n in names)


def sums(table: str, names: list[str], where: str) -> tuple[dict, list[str]]:
    """Sums of the columns, plus the columns that have NULLs in some row (a stat not recorded that season)."""
    select = ", ".join(f"SUM(CAST([{n}] AS bigint))" for n in names)
    nulls = ", ".join(f"SUM(CASE WHEN [{n}] IS NULL THEN 1 ELSE 0 END)" for n in names)
    row = sql(f"SELECT {select}, {nulls} FROM {table} WHERE {where}")[0]
    n = len(names)
    totals = {k: num(v) for k, v in zip(names, row[:n])}
    partial = [k for k, c in zip(names, row[n:]) if c and int(c) > 0]
    return totals, partial


def team(file_id: str, year: int, code: str) -> dict:
    rows = sql(f"SELECT {cols(TEAM_COLS)} FROM Teams WHERE yearID={year} AND teamID='{code}'")
    if len(rows) != 1:
        sys.exit(f"{file_id}: expected one Teams row for {year} {code}, got {len(rows)}")
    t = dict(zip(TEAM_COLS, rows[0]))
    name, park, league = t.pop("name"), t.pop("park"), t.pop("lgID")
    season = {k: num(v) for k, v in t.items()}
    where = f"yearID={year} AND teamID='{code}'"

    bat, bat_partial = sums("Batting", BAT_SUMS, where)
    pit, pit_partial = sums("Pitching", PIT_SUMS, where)
    # Catchers' lines are the team's defense against the running game.
    catchers, _ = sums("Fielding", ["SB", "CS", "PB", "WP", "E", "DP"], where + " AND POS='C'")
    # WHY: a team's total outs per start is always about 27. What a starter lasts is the outs of the pitchers who mostly
    # started, per start (their relief outs are few, and they are counted, so this runs a little high).
    starters, _ = sums("Pitching", ["IPouts", "GS"], where + " AND GS * 2 > G")
    managers = [
        {"name": f"{m[0]} {m[1]}", "g": num(m[2]), "w": num(m[3]), "l": num(m[4])}
        for m in sql("SELECT p.nameFirst, p.nameLast, m.G, m.W, m.L FROM Managers m JOIN People p ON p.playerID=m.playerID "
                     f"WHERE m.yearID={year} AND m.teamID='{code}' ORDER BY m.G DESC")
    ]

    games = season["G"]
    # Per-game rates are computed here so the benchmark and the tests compare like with like.
    rates = {
        "runsPerGame": round(season["R"] / games, 3),
        "runsAllowedPerGame": round(season["RA"] / games, 3),
        "stealsPerGame": round(season["SB"] / games, 3),
        "caughtStealingPerGame": round(season["CS"] / games, 3),
        "stealSuccess": round(season["SB"] / (season["SB"] + season["CS"]), 3) if season["SB"] + season["CS"] else None,
        "doublePlaysPerGame": round(season["DP"] / games, 3),
        "errorsPerGame": round(season["E"] / games, 3),
        "completeGameRate": round(season["CG"] / games, 3),
        "pitchersPerGame": round(pit["G"] / games, 3),
        "sacrificesPerGame": round(bat["SH"] / games, 3) if bat["SH"] is not None else None,
        "intentionalWalksPerGame": round(bat["IBB"] / games, 3) if bat["IBB"] is not None else None,
        "outsPerStart": round(starters["IPouts"] / starters["GS"], 2) if starters["GS"] else None,
    }
    return {
        "id": file_id, "year": year, "lahmanTeamId": code, "name": name, "park": park, "league": league,
        "managers": managers, "season": season, "batting": bat, "pitching": pit, "catchers": catchers,
        "notRecorded": {"batting": bat_partial, "pitching": pit_partial}, "rates": rates,
    }


def league(year: int, lg: str) -> dict:
    """The league's totals that year: the yardstick for a team's era (a 1968 run is not a 1927 run)."""
    where = f"yearID={year} AND lgID='{lg}'"
    season, _ = sums("Teams", ["G", "R", "H", "HR", "BB", "SO", "SB", "CS", "CG", "SV", "IPouts", "E", "DP",
                              "ER", "HA", "HRA", "BBA", "SOA"], where)
    bat, bat_partial = sums("Batting", ["AB", "SH", "SF", "GIDP", "IBB", "HBP"], where)
    g = season["G"]
    return {
        "year": year, "league": lg, "teamGames": g, "season": season, "batting": bat,
        "notRecorded": {"batting": bat_partial},
        # What an average pitcher did, per nine innings: the yardstick each pitcher is scaled against in the game.
        "pitchingPerNine": {
            "era": round(27 * season["ER"] / season["IPouts"], 3),
            "nonHomeRunHitsPerNine": round(27 * (season["HA"] - season["HRA"]) / season["IPouts"], 3),
            "homeRunsPerNine": round(27 * season["HRA"] / season["IPouts"], 3),
            "strikeoutsPerNine": round(27 * season["SOA"] / season["IPouts"], 3),
            "walksPerNine": round(27 * season["BBA"] / season["IPouts"], 3),
        },
        "rates": {
            "runsPerTeamGame": round(season["R"] / g, 3),
            "stealsPerTeamGame": round(season["SB"] / g, 3),
            "completeGameRate": round(season["CG"] / g, 3),
            "doublePlaysPerTeamGame": round(season["DP"] / g, 3),
            "sacrificesPerTeamGame": round(bat["SH"] / g, 3) if bat["SH"] is not None else None,
        },
    }


def main() -> None:
    teams = [team(*t) for t in TEAMS]
    leagues = {}
    for t in teams:
        leagues[f"{t['year']}-{t['league']}"] = league(t["year"], t["league"])
    doc = {
        "source": "Lahman Baseball Database (SABR, Sean Lahman), CC BY-SA 3.0; see src/MiniLeague.Web/wwwroot/data/ATTRIBUTION.md",
        "generatedBy": "tools/export_lahman_reference.py",
        "generated": date.today().isoformat(),
        "note": "Real season totals for calibration only. A stat listed under notRecorded was not kept that season, "
                "so its sum is partial: do not compare it.",
        "teams": teams,
        "leagues": leagues,
    }
    OUT.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {OUT} ({len(teams)} teams, {len(leagues)} league-seasons)")


if __name__ == "__main__":
    main()
