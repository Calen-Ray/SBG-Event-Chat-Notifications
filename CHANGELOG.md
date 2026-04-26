# Changelog

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
