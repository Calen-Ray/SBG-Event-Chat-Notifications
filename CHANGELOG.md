# Changelog

## v0.4.2

- Fix compatibility with the July 2026 Super Battle Golf engine update:
  `CourseManager.PlayerState.isSpectator` was renamed to `isInSpectatorMode`.

## v0.4.1

- Include the displaced leader in the announcement: `Alice took first on longest chip-in
  from Bob — 27m.` First-time leaders (no prior holder) still read as
  `Alice took first on longest chip-in — 27m.`.

## v0.4.0

- **Cut the noise. Only chat now when a player moves into first place** on one of the
  tab-menu leaderboard metrics the vanilla game already tracks: `bestHoleScore`,
  `longestChipIn`, `avgFinishTime`, `itemPickups`, and K/O ratio
  (`matchKnockouts / max(1, matchKnockedOut)`). Stable tie-break by `joinIndex` so a true
  tie doesn't ping-pong "Alice leads / Bob leads / Alice leads" across reconciles. Solo
  sessions stay quiet (skipped when fewer than two non-spectators are connected).
- Removed v0.3.0's pairwise score / knockouts / strokes overtake firehose, the
  per-knockout chat line, the per-shot chip-in line, and the perfect-drive `SwingNiceShot`
  VFX hook. Hole-event notifications retained: hole-in-one / albatross / eagle / birdie /
  condor (via `InfoFeed.StrokesMessageData`) and speedrun (via `SpeedrunMessageData`) —
  the rest is treated as scoreboard-noise that already has a UI surface.

## v0.3.0

- Add stat-pass notifications. Each modded client subscribes to
  `CourseManager.PlayerStatesChanged` and tracks pairwise rising-edge transitions on three
  metrics — match score, knockouts, and strokes (a.k.a. putts). When player A goes from
  strictly worse than B to tied-or-better, the chat reads
  `[Match] {A} passed {B} on {metric}.`. Score and knockouts are higher-is-better;
  strokes are lower-is-better, so passing on putts means going from "more strokes than B"
  to "fewer or equal."
- Initial-state seed is treated as neutral (no cascade of "X passed Y" on first tick).

## v0.2.0

- Add coverage for perfect swings on the driving range (and anywhere outside hole completion):
  hooked `VfxManager.PlayPooledVfxLocalOnlyInternal(SwingNiceShot)` and post a `[Match]
  {Player} hit a perfect drive!` line, with the swinger resolved by closest-player proximity
  to the VFX origin. Multi-hit swings are debounced (0.5 s + 1 m) so each shot only posts
  once.

## v0.1.0

- Initial release. Hooks `InfoFeed` RPC handlers to mirror the per-stroke / chip-in / speedrun
  / knockout events into the local text chat as narrator lines. Stat-pass tracking deferred.
