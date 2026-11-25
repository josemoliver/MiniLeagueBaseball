"""
Builds roster JSON for src/MiniLeague.Web/wwwroot/data from the Lahman Baseball Database.

Usage:  python tools/generate_teams.py <dir with Batting/Pitching/People/Appearances/Fielding/Teams.csv>

Writes teams/*.json, teams.json and stadiums.json.

Data: Sean Lahman's Baseball Database (https://sabr.org/lahman-database/), CC BY-SA.
Re-running overwrites the generated files.

Fielding ratings come from Lahman's fielding table, compared with the league average at that position
that season (and pulled toward average for small samples). Stadium dimensions are typed in below from
memory of the real parks and are approximate; the scoring factor comes from Lahman's park factors and the
home-run and extra-base factors are derived from the fence distances.
"""
import csv
import json
import sys
from collections import defaultdict
from pathlib import Path

# (file slug, Lahman teamID, year, city, nickname, abbreviation, league, stadium id)
TEAMS = [
    ("1927-nyy", "NYA", 1927, "New York",     "Yankees",   "NYY", "AL", "yankee-1927"),
    ("1955-bro", "BRO", 1955, "Brooklyn",     "Dodgers",   "BRO", "NL", "ebbets-1955"),
    ("1961-nyy", "NYA", 1961, "New York",     "Yankees",   "NYY", "AL", "yankee-1961"),
    ("1963-lad", "LAN", 1963, "Los Angeles",  "Dodgers",   "LAD", "NL", "dodger-1963"),
    ("1967-stl", "SLN", 1967, "St. Louis",    "Cardinals", "STL", "NL", "busch-1967"),
    ("1968-det", "DET", 1968, "Detroit",      "Tigers",    "DET", "AL", "tiger-1968"),
    ("1969-nym", "NYN", 1969, "New York",     "Mets",      "NYM", "NL", "shea-1969"),
    ("1970-bal", "BAL", 1970, "Baltimore",    "Orioles",   "BAL", "AL", "memorial-1970"),
    ("1973-oak", "OAK", 1973, "Oakland",      "Athletics", "OAK", "AL", "oakland-1973"),
    ("1975-cin", "CIN", 1975, "Cincinnati",   "Reds",      "CIN", "NL", "riverfront-1975"),
    ("1975-bos", "BOS", 1975, "Boston",       "Red Sox",   "BOS", "AL", "fenway-1975"),
    ("1979-pit", "PIT", 1979, "Pittsburgh",   "Pirates",   "PIT", "NL", "threerivers-1979"),
    ("1980-phi", "PHI", 1980, "Philadelphia", "Phillies",  "PHI", "NL", "veterans-1980"),
    ("1980-kc",  "KCA", 1980, "Kansas City",  "Royals",    "KC",  "AL", "royals-1980"),
    ("1982-mil", "ML4", 1982, "Milwaukee",    "Brewers",   "MIL", "AL", "county-1982"),
]

# id: (name, city, left, left-center, center, right-center, right, left wall ft, right wall ft, artificial turf)
# Approximate distances to the fences, in feet.
STADIUMS = {
    "yankee-1927":      ("Yankee Stadium (1927)", "New York", 281, 460, 490, 429, 296, 8, 8, False),
    "ebbets-1955":      ("Ebbets Field", "Brooklyn", 348, 351, 393, 355, 297, 9, 19, False),
    "yankee-1961":      ("Yankee Stadium (1961)", "New York", 301, 457, 461, 407, 296, 8, 8, False),
    "dodger-1963":      ("Dodger Stadium", "Los Angeles", 330, 380, 395, 380, 330, 8, 8, False),
    "busch-1967":       ("Busch Memorial Stadium", "St. Louis", 330, 372, 414, 372, 330, 12, 12, False),
    "tiger-1968":       ("Tiger Stadium", "Detroit", 340, 365, 440, 370, 325, 8, 8, False),
    "shea-1969":        ("Shea Stadium", "New York", 341, 371, 410, 371, 341, 8, 8, False),
    "memorial-1970":    ("Memorial Stadium", "Baltimore", 309, 360, 405, 360, 309, 8, 8, False),
    "oakland-1973":     ("Oakland-Alameda County Coliseum", "Oakland", 330, 375, 400, 375, 330, 8, 8, False),
    "riverfront-1975":  ("Riverfront Stadium", "Cincinnati", 330, 375, 404, 375, 330, 12, 12, True),
    "fenway-1975":      ("Fenway Park", "Boston", 315, 379, 390, 380, 302, 37, 5, False),
    "threerivers-1979": ("Three Rivers Stadium", "Pittsburgh", 335, 375, 400, 375, 335, 10, 10, True),
    "veterans-1980":    ("Veterans Stadium", "Philadelphia", 330, 371, 408, 371, 330, 12, 12, True),
    "royals-1980":      ("Royals Stadium", "Kansas City", 330, 375, 410, 375, 330, 8, 8, True),
    "county-1982":      ("Milwaukee County Stadium", "Milwaukee", 315, 362, 402, 362, 315, 8, 8, False),
}

FIELD_POSITIONS = {"C": "G_c", "1B": "G_1b", "2B": "G_2b", "3B": "G_3b", "SS": "G_ss",
                   "LF": "G_lf", "CF": "G_cf", "RF": "G_rf"}
BENCH_SIZE = 4
STARTERS, RELIEVERS = 4, 2  # plus one closer


def clamp(x, lo, hi):
    return max(lo, min(hi, x))


YEARS = {t[2] for t in TEAMS}
FIELD_KEYS = ["G", "PO", "A", "E", "SB", "CS"]


def load_fielding(lahman):
    """Per player-season-team and per league-season fielding totals, by position."""
    players = defaultdict(lambda: defaultdict(lambda: defaultdict(int)))
    league = defaultdict(lambda: defaultdict(int))
    for r in rows(lahman / "Fielding.csv"):
        year = int(r["yearID"])
        if year not in YEARS:
            continue
        for k in FIELD_KEYS:
            v = n(r, k)
            players[(r["playerID"], year, r["teamID"])][r["POS"]][k] += v
            league[(year, r["lgID"], r["POS"])][k] += v
    return players, league


def rating(player, lg, pos):
    """(range 1-9, error rating 0-3, arm 1-5) against the league average at the position."""
    g = player["G"]
    lg_ch = lg["PO"] + lg["A"]
    lg_g = max(lg["G"], 1)
    lg_cpg = lg_ch / lg_g if lg_ch else 1.0
    chances = player["PO"] + player["A"]
    cpg = chances / g if g else lg_cpg

    k = 40  # games of "average" mixed in, so a handful of games cannot make a star or a disaster
    idx = (g * (cpg / lg_cpg) + k) / (g + k)
    rng = int(clamp(round(5 + (idx - 1) / 0.09), 1, 9))

    lg_err = lg["E"] / max(lg_ch + lg["E"], 1)
    kc = 150
    err_rate = (player["E"] + kc * lg_err) / (chances + player["E"] + kc)
    e_idx = err_rate / lg_err if lg_err else 1.0
    err = 3 if e_idx <= 0.75 else 2 if e_idx <= 1.12 else 1 if e_idx <= 1.5 else 0

    arm_idx = 1.0
    if pos == "C":
        att, lg_att = player["SB"] + player["CS"], lg["SB"] + lg["CS"]
        if lg_att > 0 and lg["CS"] > 0:
            lg_pct = lg["CS"] / lg_att
            arm_idx = ((player["CS"] + 30 * lg_pct) / (att + 30)) / lg_pct
    elif g:
        lg_apg = lg["A"] / lg_g if lg["A"] else 0
        if lg_apg:
            arm_idx = (g * (player["A"] / g / lg_apg) + k) / (g + k)
    arm = 1 if arm_idx <= 0.7 else 2 if arm_idx <= 0.9 else 3 if arm_idx <= 1.1 else 4 if arm_idx <= 1.3 else 5
    return [rng, err, arm]


def fielding_map(pid, year, tid, lg_id, field, appearances):
    """{position: [range, errorRating, arm]} for the positions he played for this team that season."""
    players, league = field
    by_pos = players.get((pid, year, tid), {})
    out = {}
    for pos in FIELD_POSITIONS:
        if pos in by_pos and by_pos[pos]["G"] >= 5:
            out[pos] = rating(by_pos[pos], league[(year, lg_id, pos)], pos)
    # Older seasons list outfielders as "OF" only: use that line for the outfield spots he played.
    if "OF" in by_pos:
        a = appearances.get(pid, {})
        for pos, col in (("LF", "G_lf"), ("CF", "G_cf"), ("RF", "G_rf")):
            if pos not in out and (n(a, col) >= 5 or not a):
                out[pos] = rating(by_pos["OF"], league[(year, lg_id, "OF")], "OF")
    return out


def era_rates(lahman, year, lg_id):
    """(errors, strikeouts) per team game for the league that season."""
    e = so = g = 0
    for r in rows(lahman / "Teams.csv"):
        if int(r["yearID"]) == year and r["lgID"] == lg_id:
            e += n(r, "E")
            so += n(r, "SOA")
            g += n(r, "G")
    return (round(e / g, 2), round(so / g, 2)) if g else (0.8, 4.0)


def league_pitching(lahman, year, lg_id):
    """What an average pitcher did in the league that season, per nine innings (the engine scales each pitcher by it)."""
    er = ha = hra = bba = soa = outs = 0
    for r in rows(lahman / "Teams.csv"):
        if int(r["yearID"]) == year and r["lgID"] == lg_id:
            er += n(r, "ER")
            ha += n(r, "HA")
            hra += n(r, "HRA")
            bba += n(r, "BBA")
            soa += n(r, "SOA")
            outs += n(r, "IPouts")
    if not outs:
        return None  # the engine falls back to a 4.00 ERA, 7.0 strikeouts and 3.0 walks per nine
    return dict(era=round(27 * er / outs, 3), nonHomeRunHitsPerNine=round(27 * (ha - hra) / outs, 3),
                homeRunsPerNine=round(27 * hra / outs, 3), strikeoutsPerNine=round(27 * soa / outs, 3),
                walksPerNine=round(27 * bba / outs, 3))


def league_steal_rate(lahman, year, lg_id):
    """Share of steal attempts that succeeded in the league that season; None when caught stealing was not recorded."""
    sb = cs = 0
    for r in rows(lahman / "Teams.csv"):
        if int(r["yearID"]) == year and r["lgID"] == lg_id:
            sb += n(r, "SB")
            cs += n(r, "CS")
    return round(sb / (sb + cs), 3) if sb + cs and cs else None


def park_factors(lahman, year, tid):
    for r in rows(lahman / "Teams.csv"):
        if int(r["yearID"]) == year and r["teamID"] == tid:
            bpf, ppf = n(r, "BPF"), n(r, "PPF")
            return round((bpf + ppf) / 200, 3) if bpf and ppf else 1.0
    return 1.0


def hr_factor(pull_dist, alley_dist, wall):
    """Short fences and low walls help homers; deep alleys and tall walls hurt."""
    f = 1 + 0.010 * (330 - pull_dist) + 0.004 * (375 - alley_dist) - 0.006 * (wall - 8)
    return round(clamp(f, 0.80, 1.25), 3)


def stadium_json(sid, runs):
    name, city, left, lcf, cf, rcf, right, lwall, rwall, turf = STADIUMS[sid]
    dt = 1 + 0.0025 * ((lcf + rcf) / 2 - 375) + 0.0015 * (cf - 400) + (0.05 if turf else 0)
    return dict(
        id=sid, name=name, city=city,
        left=left, leftCenter=lcf, center=cf, rightCenter=rcf, right=right,
        leftWall=lwall, rightWall=rwall,
        hrLeft=hr_factor(right, rcf, rwall),      # left-handed batters pull to right field
        hrRight=hr_factor(left, lcf, lwall),      # right-handed batters pull to left field
        doublesTriples=round(clamp(dt, 0.85, 1.30), 3),
        singles=1.03 if turf else 1.0,
        runs=runs, turf=turf)


def rows(path):
    with open(path, encoding="utf-8-sig", newline="") as f:
        yield from csv.DictReader(f)


def n(row, key):
    v = row.get(key, "")
    return int(float(v)) if v not in ("", None) else 0


def load(lahman, name, team_id, year):
    return [r for r in rows(lahman / name) if r["teamID"] == team_id and int(r["yearID"]) == year]


def sum_rows(rs, keys):
    out = defaultdict(int)
    for r in rs:
        for k in keys:
            out[k] += n(r, k)
    return out


def speed_rating(ab, sb, triples):
    score = (sb + 3 * triples) * 600 / max(ab, 300)
    return 5 if score >= 60 else 4 if score >= 36 else 3 if score >= 16 else 2 if score >= 6 else 1


def ops(b):
    ab, h, bb, hbp = b["ab"], b["h"], b["bb"], b["hbp"]
    pa = ab + bb + hbp
    if ab == 0 or pa == 0:
        return 0.0
    tb = h + b["d2"] + 2 * b["d3"] + 3 * b["hr"]
    return (h + bb + hbp) / pa + tb / ab


def build_team(lahman, people, field, slug, tid, year, city, nick, abbr, lg, stadium, team_no):
    bat_rows = defaultdict(list)
    for r in load(lahman, "Batting.csv", tid, year):
        bat_rows[r["playerID"]].append(r)
    app = {r["playerID"]: r for r in load(lahman, "Appearances.csv", tid, year)}
    pit_rows = defaultdict(list)
    for r in load(lahman, "Pitching.csv", tid, year):
        pit_rows[r["playerID"]].append(r)

    def display_name(pid):
        p = people[pid]
        return f'{p["nameFirst"][:1]}. {p["nameLast"]}'

    # ── Batters ───────────────────────────────────────────────────────────
    batters = {}
    for pid, rs in bat_rows.items():
        s = sum_rows(rs, ["AB", "H", "2B", "3B", "HR", "RBI", "SB", "CS", "BB", "SO", "HBP", "SH"])
        batters[pid] = dict(pid=pid, ab=s["AB"], h=s["H"], d2=s["2B"], d3=s["3B"], hr=s["HR"],
                            rbi=s["RBI"], sb=s["SB"], cs=s["CS"], bb=s["BB"], so=s["SO"], hbp=s["HBP"],
                            sh=s["SH"])

    # Fielding games by position (Appearances), position players only.
    pitchers_only = {pid for pid, rs in pit_rows.items()
                     if pid not in batters or batters[pid]["ab"] < 150}
    cand = {pid: b for pid, b in batters.items() if pid not in pitchers_only and b["ab"] >= 40}

    pairs = []
    for pid in cand:
        a = app.get(pid)
        if not a:
            continue
        for pos, col in FIELD_POSITIONS.items():
            g = n(a, col)
            if g:
                pairs.append((g, pid, pos))
    pairs.sort(reverse=True)
    starters, used = {}, set()
    for g, pid, pos in pairs:
        if pos not in starters and pid not in used:
            starters[pos] = pid
            used.add(pid)
    missing = [p for p in FIELD_POSITIONS if p not in starters]
    if missing:
        raise SystemExit(f"{slug}: could not fill positions {missing}")

    rest = sorted((b for pid, b in cand.items() if pid not in used), key=lambda b: -b["ab"])
    dh = max(rest[:4], key=ops)  # best bat among the top bench regulars by at-bats
    used.add(dh["pid"])
    bench = [b for b in sorted((b for pid, b in cand.items() if pid not in used), key=lambda b: -b["ab"])][:BENCH_SIZE]

    # Batting order: OPS ranks r1..r9 -> [r4, r3, r1, r2, r5..r9]
    nine = [(batters[pid], pos) for pos, pid in starters.items()] + [(dh, "DH")]
    nine.sort(key=lambda t: -ops(t[0]))
    order = [nine[3], nine[2], nine[0], nine[1]] + nine[4:]

    def batter_json(b, pos, pid_num):
        p = people[b["pid"]]
        return dict(
            id=team_no * 100 + pid_num, name=display_name(b["pid"]), pos=pos,
            bats={"L": "Left", "R": "Right", "B": "Switch"}.get(p["bats"], "Right"),
            throws="Left" if p["throws"] == "L" else "Right",
            ab=b["ab"], h=b["h"], hr=b["hr"], bb=b["bb"], so=b["so"], d2=b["d2"], d3=b["d3"],
            sb=b["sb"], speed=speed_rating(b["ab"], b["sb"], b["d3"]),
            rbi=b["rbi"], cs=b["cs"], hbp=b["hbp"], sh=b["sh"],
            field=fielding_map(b["pid"], year, tid, lg, field, app))

    def bench_pos(b):
        a = app.get(b["pid"], {})
        best = max(FIELD_POSITIONS.items(), key=lambda kv: n(a, kv[1]))
        return best[0] if n(a, best[1]) else "DH"

    batter_out = [batter_json(b, pos, i + 1) for i, (b, pos) in enumerate(order)]
    batter_out += [batter_json(b, bench_pos(b), 10 + i) for i, b in enumerate(bench)]

    # ── Pitchers ──────────────────────────────────────────────────────────
    pit = {}
    for pid, rs in pit_rows.items():
        s = sum_rows(rs, ["W", "L", "G", "GS", "CG", "SV", "IPouts", "H", "ER", "HR", "BB", "SO"])
        if s["IPouts"] == 0:
            continue
        pit[pid] = dict(pid=pid, **{k.lower(): v for k, v in s.items()})

    starters_p = sorted((p for p in pit.values() if p["gs"] >= 5), key=lambda p: (-p["gs"], -p["ipouts"]))[:STARTERS]
    chosen = {p["pid"] for p in starters_p}
    relief = [p for p in pit.values() if p["pid"] not in chosen]
    closer = max(relief, key=lambda p: (p["sv"], p["g"])) if relief else None
    if closer:
        chosen.add(closer["pid"])
    others = sorted((p for p in pit.values() if p["pid"] not in chosen), key=lambda p: -p["ipouts"])[:RELIEVERS]

    def pitcher_json(p, role, pid_num):
        ip = p["ipouts"] / 3
        if role == "Starter":
            tf = max(1, min(9, int(10 - p["ipouts"] / 3 / max(p["gs"], 1) + 0.5)))
        else:
            tf = 5
        # His own batting line (a pitcher bats when a game has no designated hitter). Pitchers have Batting rows too.
        brs = bat_rows.get(p["pid"], [])
        bs = sum_rows(brs, ["AB", "H", "2B", "3B", "HR", "BB", "SO", "HBP", "SH"]) if brs else None
        bat = None if not bs or bs["AB"] == 0 else dict(
            ab=bs["AB"], h=bs["H"], d2=bs["2B"], d3=bs["3B"], hr=bs["HR"], bb=bs["BB"], so=bs["SO"],
            hbp=bs["HBP"], sh=bs["SH"])
        bats = {"L": "Left", "R": "Right", "B": "Switch"}.get(people[p["pid"]]["bats"])
        out = dict(
            id=team_no * 100 + pid_num, name=display_name(p["pid"]),
            throws="Left" if people[p["pid"]]["throws"] == "L" else "Right",
            era=round(p["er"] * 9 / ip, 2), ip=round(ip, 1), h=p["h"], bb=p["bb"], so=p["so"],
            tiringFactor=tf, role=role, wins=p["w"], losses=p["l"], saves=p["sv"], cg=p["cg"], hr=p["hr"])
        if role == "Starter":
            out["gs"] = p["gs"]   # games started: how long his real starts were sets when he tires (FatigueModel.Stamina)
        if bats:
            out["bats"] = bats
        if bat:
            out["bat"] = bat
        return out

    pitcher_out = [pitcher_json(p, "Starter", 50 + i) for i, p in enumerate(starters_p)]
    if closer:
        pitcher_out.append(pitcher_json(closer, "Closer", 60))
    pitcher_out += [pitcher_json(p, "Reliever", 61 + i) for i, p in enumerate(others)]

    # ── How the club was managed that season (team totals) ────────────────
    team_row = load(lahman, "Teams.csv", tid, year)[0]
    games = max(n(team_row, "G"), 1)
    tendencies = dict(
        stealAttemptsPerGame=round((n(team_row, "SB") + n(team_row, "CS")) / games, 3),
        sacrificesPerGame=round(sum(b["sh"] for b in batters.values()) / games, 3),
        completeGameRate=round(n(team_row, "CG") / games, 3),
        savesPerGame=round(n(team_row, "SV") / games, 3))

    return dict(id=team_no, city=city, nickname=nick, abbreviation=abbr, season=year, league=lg,
                stadium=stadium, eraErrorsPerGame=era_rates(lahman, year, lg)[0],
                eraStrikeoutsPerGame=era_rates(lahman, year, lg)[1],
                leaguePitching=league_pitching(lahman, year, lg),
                leagueStealRate=league_steal_rate(lahman, year, lg),
                tendencies=tendencies,
                batters=batter_out, pitchers=pitcher_out)


def main():
    lahman = Path(sys.argv[1])
    out_dir = Path(__file__).resolve().parent.parent / "src" / "MiniLeague.Web" / "wwwroot" / "data"
    (out_dir / "teams").mkdir(parents=True, exist_ok=True)
    people = {r["playerID"]: r for r in rows(lahman / "People.csv")}

    field = load_fielding(lahman)
    stadiums = []
    index = []
    for team_no, (slug, tid, year, city, nick, abbr, lg, stadium) in enumerate(TEAMS, start=1):
        team = build_team(lahman, people, field, slug, tid, year, city, nick, abbr, lg, stadium, team_no)
        stadiums.append(stadium_json(stadium, park_factors(lahman, year, tid)))
        (out_dir / "teams" / f"{slug}.json").write_text(json.dumps(team, indent=2) + "\n", encoding="utf-8")
        index.append(f"teams/{slug}.json")
        print(f"{slug}: {len(team['batters'])} batters, {len(team['pitchers'])} pitchers")
    (out_dir / "teams.json").write_text(json.dumps({"teams": index}, indent=2) + "\n", encoding="utf-8")
    (out_dir / "stadiums.json").write_text(json.dumps({"stadiums": stadiums}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
