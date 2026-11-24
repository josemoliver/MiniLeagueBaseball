# Showing the game on a TV (Chromecast)

The Cast button in the app bar shows the game on a Chromecast or a TV with Cast built in, while you keep playing on your phone or computer. The TV shows the score, the field with the runners, who is up, who is pitching and the last play.

## Using it

1. Open the game in Chrome or Edge (over HTTPS, or `localhost`). Other browsers do not show the button.
2. Tap the Cast button in the app bar and pick the TV. The button turns orange while casting; tap it again to stop.
3. Play as usual. The TV follows every play, substitution and injury.

No Cast button, or no TV in the list? Open **TV view** from the menu (it shows up once casting has failed, or when the browser cannot cast). It opens the same screen in a new tab, which you can cast with the browser's own menu (Chrome: Cast, then Cast tab). It updates by itself from the game tab.

## How it works

- `js/cast.js` asks the browser for a Cast screen with the Presentation API (`PresentationRequest`) and has it open the `/tv` page. No Cast app registration is needed.
- `CastService` turns the game into a trimmed save (`GameSnapshots`, without the long history) and `GameRoom.SaveGame` hands it over after every change, together with which field is showing.
- Messages go to the TV over the presentation connection and over a `BroadcastChannel`, which is what feeds a TV view opened by hand. A TV page that joins late asks for the latest game.
- `Pages/Tv.razor` rebuilds the game from the save and draws the same field the player has chosen: the Classic `FieldView`, or the Detailed `DetailedFieldView`.
- **Detailed view:** `js/cast.js` wraps `mlbAnimator.play`, `show` and `skip`, so every animated play and scene (and a skip) is passed on and played on the TV's canvas, in order. The new score, outs and batter reach the TV when the field is drawn again after the play, so the TV never shows the result ahead of the animation.
- **The field leaves the player's screen while casting.** `GameRoom` parks the field region off-screen (the Classic field is simply not drawn) and the layout reflows to give the decisions and lineups the room; a small "The field is on the TV" note appears in the scoreboard. The animated canvas stays mounted off-screen on purpose: it still draws the plays that are passed on to the TV and keeps the game's pace.
- **Sound comes from the TV.** `mlbSound.play` is wrapped the same way: once the TV says hello over the presentation connection, each sound (including the animation's cues and the organ tunes) is sent to it and this device goes quiet through `mlbSound.setSilenced`, which is not saved and still reports how long each sound runs, so the game's timing does not change. If the browser will not let the TV make sound (it can block audio until someone interacts with the page), the TV says so and the sound returns to this device. A muted game stays muted. A TV view opened by hand (no presentation connection) stays silent, so two tabs on one computer never play everything twice.

## Limits

- The TV's own canvas is not clickable (there is nothing to skip or replay there; the player does that on their device).
- Checked in Chrome with two windows (Classic and Detailed: the TV followed the game, showed the animated plays and updated the score after them). The Classic field on a real Chromecast works. **The Detailed field and the TV's sound are not yet tried on one.** Whether a Cast page may play audio without a click is the open question; the fallback above covers it, but then the sound stays on the player's device.
- `node --test tests/js/cast.test.js` covers the relay (order, late joiners, skips, no echo).
