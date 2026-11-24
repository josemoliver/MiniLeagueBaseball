<p align="center">
  <img src="src/MiniLeague.Web/wwwroot/images/minileague-baseball.webp" alt="MiniLeague Baseball: a batter swings as the pitcher delivers, the catcher and umpire crouched behind him" width="480" />
</p>

<h1 align="center">MiniLeague Baseball</h1>

<p align="center">
  Historical baseball simulation powered by public historical statistics.<br />
  Be the manager. Put the 1927 Yankees up against the 1982 Brewers, call every pitch and strategy, and watch real statistics decide the result.
</p>

<p align="center">
  <strong>Play now: <a href="https://josemoliver.github.io/MiniLeagueBaseball/">https://josemoliver.github.io/MiniLeagueBaseball/</a></strong>
</p>

MiniLeague Baseball is a **manager's game, not an arcade game**: you never swing or throw. You choose the lineup, the starting pitcher, the pitch, the defensive alignment, the steal, the bunt, the pinch hitter and the pitching change, and a probability engine fed with real season statistics resolves each plate appearance.

It runs **entirely in the browser** (Blazor WebAssembly), so it needs no server, installs like an app, and works offline.

<p align="center">
  <img src="docs/images/detailed-gameplay.png" alt="A game in progress in the Detailed view: a home run leaves Ebbets Field, the runners trot around and teammates come out to meet them, with the scoreboard, the defensive strategy buttons and the batter's card beside the field" width="860" />
</p>

## Features

**Teams and data**
- 15 classic teams from 1927 to 1982, with real batting, pitching and fielding statistics from the [Lahman Baseball Database](https://sabr.org/lahman-database/).
- Each team plays in its real ballpark, with its fence distances and park factors for home runs, extra-base hits and overall scoring.
- Add your own teams and parks by dropping in a JSON file (see [Adding teams](#adding-teams-and-ballparks)).

**Managing the game**
- Offense: swing away, aggressive, safe, steal, hit and run, sacrifice. Defense: pitch type, infield in, corners in, guard the lines, intentional walk.
- Edit the batting order and fielding positions before the game, and pick any pitcher to start.
- Pinch hit, pinch run, replace a fielder, swap positions. A reliever has to warm up first: too little and he comes in cold, too long and he is stale.
- Play as the visitors, the home team, both teams (two players, one screen), or watch the computer play itself.
- Keyboard shortcuts for everything, with printable cards (menu → **Keyboard Cards**).

**Realism**
- Pitchers tire by pitch count and their own durability, and a computer manager pulls them.
- Fielding matters: range, error rates and arm strength per position. Errors happen at each season's real rate, and a bench bat playing shortstop is a liability.
- Optional random events: injuries that force a replacement, rain delays that can end a game, and manager ejections that hand your team to the computer.
- A computer manager that pinch hits, pinch runs, warms up and changes pitchers, and tidies its defense after substitutions.

**Watching the game**
- Two ways to see each play. **Detailed** (the default) is a view from behind home plate in the spirit of the 1984 original: little players run to the ball, throw, slide, tag and steal, the ball casts a shadow on the field, the outfield plays deeper for a power hitter, and a message box announces each play. Each ballpark is drawn to its real fence distances, which are painted on the wall (curving with it), with foul poles, bullpens and the infield's base paths and bases. The game opens with the lineups and "Play ball!", teams run on and off between innings, relievers and pinch hitters walk out, home runs are met at the plate, a walk-off is mobbed, and a score card closes the game. **Classic** is the overhead view with the ball's path and the runners moving.
- Switch between them at any time from the buttons above the field (or on the setup screen); your choice is remembered on this device. In Detailed, pick **Fast**, **Normal** or **Slow**, click the field or press **Skip** to jump to the end of a play, and press **Replay** (in the corner of the field, between plays) to see the last play again.
- Detailed respects a device's "reduce motion" setting (no sparkle or trails, and it starts at Fast), and announces each play to screen readers. Prefer the simpler overhead view? Press **CLASSIC**.

**On the big screen**
- Cast the field to a Chromecast, or a TV with Cast built in, from the Cast button in the app bar (Chrome or Edge). The TV shows the score, outs, the field with the runners, who is up, who is pitching and the last play, in whichever view you chose: **Classic**, or **Detailed** with every play animated as it happens.
- Your own screen gives the field up while you cast, so the decisions and lineups get the room, and you play from your phone or laptop with the game on the TV.
- The sounds and the organ play on the TV. If the browser will not let the TV make sound, they stay on your device.
- No Cast button, or no TV in the list? Open **TV view** from the menu (it appears when casting fails or the browser cannot cast) and cast that tab from the browser's own menu.
- How it works, and its limits, are in [docs/chromecast-support.md](docs/chromecast-support.md).

**After and around the game**
- A full box score with earned runs, decisions (W, L, S) and left on base, as plain text you can copy or print.
- A game recap: a win-probability chart, the story of the game, the biggest plays and the standout players.
- Autosave to the browser; pick the game up where you left it.
- Sound: synthesized effects, and a ballpark organ that plays *Charge!* at the start, *Take Me Out to the Ball Game* at the seventh-inning stretch, the Mexican Hat Dance for a rally, and more.
- Layouts for phones, laptops and wide monitors. Installable as an app (PWA) and playable offline.

## Screenshots

The **Detailed** view, from behind home plate. Every picture is drawn from what the simulation decided, so the animation always agrees with the box score.

<table>
  <tr>
    <td align="center" width="50%">
      <img src="docs/images/detailed-play-ball.webp" alt="Both lineups standing along their foul lines at Ebbets Field before the first pitch" width="420" /><br />
      <sub><b>Play ball!</b> Both lineups come out and stand along the foul lines, then the home team runs to its positions.</sub>
    </td>
    <td align="center" width="50%">
      <img src="docs/images/detailed-home-run.webp" alt="A home run: the crowd is on its feet and the batter's teammates have come out to meet him at the plate" width="420" /><br />
      <sub><b>A home run.</b> The crowd gets to its feet and the team comes out to meet the batter at the plate.</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="docs/images/detailed-diving-catch.webp" alt="The right fielder lies on the grass after a diving catch, under the banner DIVING CATCH" width="420" /><br />
      <sub><b>A diving catch.</b> Fielders with more range dive for more of the balls they catch.</sub>
    </td>
    <td align="center">
      <img src="docs/images/detailed-pitching-change.webp" alt="The manager and the new pitcher meet the old pitcher on the mound, under the banner naming the reliever" width="420" /><br />
      <sub><b>A pitching change.</b> The manager walks out, the reliever jogs in from the bullpen, and the old pitcher leaves with the manager.</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="docs/images/detailed-rain-delay.webp" alt="Rain falls over a darkened field as the players run for the dugouts" width="420" /><br />
      <sub><b>A rain delay</b> (an optional random event). The players run for cover and come back when it stops.</sub>
    </td>
    <td align="center">
      <img src="docs/images/detailed-final.webp" alt="The final score card, with the winning, losing and saving pitchers" width="420" /><br />
      <sub><b>The final.</b> A score card with the result and the winning, losing and saving pitchers.</sub>
    </td>
  </tr>
</table>

The **Classic** view, the overhead diagram with the ball's path and the runners moving, is one click away:

<p align="center">
  <img src="docs/images/gameplay.png" alt="The Classic view: the overhead field with a fly out, the decision panel, the play-by-play log and both lineups" width="860" />
</p>

## The teams

| Year | Team | Ballpark |
|---|---|---|
| 1927 | New York Yankees | Yankee Stadium |
| 1955 | Brooklyn Dodgers | Ebbets Field |
| 1961 | New York Yankees | Yankee Stadium |
| 1963 | Los Angeles Dodgers | Dodger Stadium |
| 1967 | St. Louis Cardinals | Busch Memorial Stadium |
| 1968 | Detroit Tigers | Tiger Stadium |
| 1969 | New York Mets | Shea Stadium |
| 1970 | Baltimore Orioles | Memorial Stadium |
| 1973 | Oakland Athletics | Oakland-Alameda County Coliseum |
| 1975 | Cincinnati Reds | Riverfront Stadium |
| 1975 | Boston Red Sox | Fenway Park |
| 1979 | Pittsburgh Pirates | Three Rivers Stadium |
| 1980 | Philadelphia Phillies | Veterans Stadium |
| 1980 | Kansas City Royals | Royals Stadium |
| 1982 | Milwaukee Brewers | Milwaukee County Stadium |

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Node.js 20 or later is only needed to run the JavaScript tests, and Python 3 only to regenerate the team data.

```bash
git clone <this repository>
cd MiniLeagueBaseball

dotnet run --project src/MiniLeague.Web
```

Then open <http://localhost:5248>. On Windows, `./build.ps1 -Action Run 5001` does the same on a port you choose (`Build`, `Restore` and `Clean` are also available).

### Playing

1. Press **PLAY BALL**, choose the two teams, who you manage, the ballpark and the starting pitchers, and optionally edit either lineup.
2. On your turn pick a strategy and press **PITCH**. Use **SUBS** for the bench, the bullpen and position changes, and **Box score** or **Recap** at any time.
3. Prefer the keyboard? Offense: `1`–`6` pick the strategy. Defense: `1`–`4` the pitch, `5`–`8` the alignment, `9` an intentional walk. `Enter` or `P` plays the pitch, `S` opens substitutions, `B` the box score. If you manage both teams, hold `Shift` for the defense keys.
4. Want it on the TV? Press the **Cast** icon in the app bar and pick your Chromecast. The field moves to the TV, you keep playing here, and the icon turns orange while you cast; press it again to stop. Casting needs Chrome or Edge, a secure page (the published site, or `localhost` when you run it yourself), and the TV on the same Wi-Fi network as your device.

## How it works

Each plate appearance is a weighted roll over the possible outcomes. The batter's per-appearance rates are scaled by the pitcher's quality (his ERA, walks and strikeouts, reduced by fatigue), then adjusted for the ballpark, the pitch chosen, the defensive alignment and the fielders' range. A ball hit to a fielder for an out can become an error at that season's real rate, and the runners then advance according to speed, outfield arms and the manager's strategy.

The computer manager is rule based. It decides situationally: bunt, steal, pinch hit, warm up a reliever, bring in the closer for a save, or pull a starter who is tiring or getting shelled.

## Project layout

```
src/
  MiniLeague.Engine/     The game itself: models, simulation, computer manager, game flow,
                         box score, recap and win probability. A plain class library with no UI.
                         Animation/ turns a finished play into a script (who goes where, and when) for the Detailed view.
  MiniLeague.Web/        The Blazor WebAssembly app: pages, components, sounds, PWA files,
    wwwroot/data/          team and ballpark JSON
tests/
  MiniLeague.Engine.Tests/   xUnit tests for the engine, including hundreds of simulated games
  MiniLeague.Web.Tests/      bUnit tests that render the real pages and dialogs
  MiniLeague.Web.E2E/        Playwright tests that drive the running app in Chrome (not part of the solution: they are slower)
  js/                        Node tests for the sound code, the offline service worker, the Detailed view's player and casting to a TV
tools/
  generate_teams.py      Builds the team, ballpark and fielding JSON from the Lahman database
  sound-preview.html     Open in a browser to hear every sound and organ tune
docs/                    The original planning document, the Animated Plays specification, and screenshots
```

## Adding teams and ballparks

Every team is a JSON file in `src/MiniLeague.Web/wwwroot/data/teams/`, listed in `teams.json`. The first nine entries in `batters` are the starting lineup in batting order and the rest are the bench:

```json
{
  "id": 11, "city": "Boston", "nickname": "Red Sox", "abbreviation": "BOS",
  "season": 1975, "league": "AL", "stadium": "fenway-1975",
  "batters": [
    { "id": 1101, "name": "J. Rice", "pos": "LF", "bats": "Right", "throws": "Right",
      "ab": 564, "h": 174, "hr": 22, "bb": 36, "so": 122, "d2": 29, "d3": 4, "sb": 10, "speed": 3,
      "field": { "LF": [4, 3, 3] } }
  ],
  "pitchers": [
    { "id": 1150, "name": "L. Tiant", "throws": "Right", "era": 4.02, "ip": 260, "h": 262, "bb": 72, "so": 142,
      "tiringFactor": 3, "role": "Starter", "wins": 18, "losses": 14 }
  ]
}
```

Ballparks live in `stadiums.json` (fence distances, wall heights and park factors). To rebuild all 15 shipped teams from the Lahman CSV files (`Batting`, `Pitching`, `People`, `Appearances`, `Fielding` and `Teams`):

```bash
python tools/generate_teams.py <folder with the Lahman CSV files>
```

## Tests

```bash
dotnet test MiniLeagueBaseball.slnx                              # engine and UI tests
node --test tests/js/sound.test.js tests/js/pwa.test.js tests/js/animator.test.js tests/js/cast.test.js   # sound, offline, animated-field and casting tests
dotnet test tests/MiniLeague.Web.E2E                             # the whole app in a real browser (about 6 minutes)
```

The browser tests start the app on a free port and drive the Chrome installed on your machine, so nothing needs downloading; set `E2E_BROWSER_CHANNEL` to use another browser (for example `msedge`). They stand in for a TV with a pretend Cast connection, so a real Chromecast still has to be tried by hand.

The engine tests play hundreds of full games and check that the rules always hold: every game ends legally, the box score adds up, runners are never lost, and errors, pitching changes and random events happen at realistic rates. To hear the sounds, open `tools/sound-preview.html` in a browser.

## Deployment

The game is a static site and is published to **GitHub Pages** at <https://josemoliver.github.io/MiniLeagueBaseball/>. The workflow in `.github/workflows/pages.yml` runs the tests, publishes with `dotnet publish src/MiniLeague.Web -c Release -o publish`, sets the project-site base path, and deploys on every push to `main`. Pages must be set to "GitHub Actions" as its source in the repository settings.

Any other static host works too. Configure it to send unknown paths to `index.html`.

## Data, credits and disclaimer

- Player and team statistics come from the [Lahman Baseball Database](https://sabr.org/lahman-database/) by Sean Lahman, used under [CC BY-SA](https://creativecommons.org/licenses/by-sa/3.0/). Ballpark fence distances were entered by hand from memory of the real parks and are approximate. Details are in [`ATTRIBUTION.md`](src/MiniLeague.Web/wwwroot/data/ATTRIBUTION.md).
- The organ melodies are public-domain compositions; their sources are credited in the same file.
- This is an independent fan project. It is not affiliated with or endorsed by Major League Baseball, any club or the players' association.

## License

The source code is released under the [MIT License](LICENSE). The team data derived from the Lahman database (`src/MiniLeague.Web/wwwroot/data`) remains licensed [CC BY-SA](https://creativecommons.org/licenses/by-sa/3.0/); see [`ATTRIBUTION.md`](src/MiniLeague.Web/wwwroot/data/ATTRIBUTION.md).
