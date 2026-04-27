using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace EventChatNotifications
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "sbg.eventchatnotifications";
        public const string ModName = "EventChatNotifications";
        public const string ModVersion = "0.4.1";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            new Harmony(ModGuid).PatchAll();
            FirstPlaceTracker.Hook();
            Log.LogInfo($"{ModName} v{ModVersion} loaded.");
        }

        private void OnDestroy()
        {
            FirstPlaceTracker.Unhook();
        }
    }

    // Watches CourseManager.PlayerStates and posts a chat line when a *new* player takes
    // first place on one of the leaderboard metrics that the game already syncs:
    //   - bestHoleScore   (StrokesUnderParType, higher enum = better)
    //   - longestChipIn   (metres, higher = better; default float.MinValue)
    //   - avgFinishTime   (seconds, lower = better; default 0 = no finishes yet)
    //   - itemPickups     (count, higher = better)
    //   - K/O ratio       (matchKnockouts / max(1, matchKnockedOut), higher = better)
    //
    // No Mirror traffic — every modded peer reads the same SyncList and renders locally.
    // 0.3.0 announced every pairwise overtake on score / knockouts / strokes, which was
    // intentional firehose during scoring; 0.4.0 narrows to "moved into first" on metrics
    // a player would actually brag about.
    internal static class FirstPlaceTracker
    {
        private enum Metric { BestHoleScore, LongestChipIn, AvgFinishTime, ItemPickups, KORatio }

        private static readonly Metric[] AllMetrics =
        {
            Metric.BestHoleScore, Metric.LongestChipIn, Metric.AvgFinishTime,
            Metric.ItemPickups, Metric.KORatio,
        };

        // Per-metric leader. Absent key = no leader yet (no eligible player).
        private static readonly Dictionary<Metric, ulong> CurrentLeader = new Dictionary<Metric, ulong>();
        private static bool subscribed;

        internal static void Hook()
        {
            if (subscribed) return;
            CourseManager.PlayerStatesChanged += OnPlayerStatesChanged;
            subscribed = true;
        }

        internal static void Unhook()
        {
            if (!subscribed) return;
            CourseManager.PlayerStatesChanged -= OnPlayerStatesChanged;
            CurrentLeader.Clear();
            subscribed = false;
        }

        private static void OnPlayerStatesChanged(SyncList<CourseManager.PlayerState>.Operation op, int index, CourseManager.PlayerState changed)
        {
            SyncList<CourseManager.PlayerState> states = CourseManager.PlayerStates;
            if (states == null) return;

            // No leader concept makes sense in solo sessions; bail until at least two
            // non-spectators are present.
            int active = 0;
            for (int i = 0; i < states.Count; i++)
                if (!states[i].isSpectator && states[i].isConnected) active++;
            if (active < 2) return;

            foreach (Metric metric in AllMetrics)
                Reconcile(states, metric);
        }

        private static void Reconcile(SyncList<CourseManager.PlayerState> states, Metric metric)
        {
            if (!TryComputeLeader(states, metric, out ulong leaderGuid, out string valueLabel))
                return; // No eligible leader yet (everyone still at default).

            bool hadPrev = CurrentLeader.TryGetValue(metric, out ulong prevLeader);
            if (hadPrev && prevLeader == leaderGuid)
                return;

            CurrentLeader[metric] = leaderGuid;

            string playerName = Narrator.PlayerNameFromGuid(leaderGuid);
            string label = MetricLabel(metric);
            // Two phrasings: "from X" when we're displacing a real previous leader, plain
            // "took first" otherwise. Self-displacement (prevLeader == leaderGuid) is filtered
            // upstream by the equality check above, so we don't need to handle it here.
            string line = hadPrev
                ? $"{playerName} took first on {label} from {Narrator.PlayerNameFromGuid(prevLeader)} — {valueLabel}."
                : $"{playerName} took first on {label} — {valueLabel}.";
            Narrator.Post(line);
        }

        // Returns true and sets out-params iff at least one player has a non-default value
        // on this metric. Ties (two players with the exact same best value) keep whichever
        // was already the leader if applicable, otherwise pick the lower joinIndex for
        // determinism — but never *announce* a tie as a new leader (the caller's caching
        // takes care of suppressing the no-op).
        private static bool TryComputeLeader(SyncList<CourseManager.PlayerState> states, Metric metric, out ulong leaderGuid, out string valueLabel)
        {
            leaderGuid = 0;
            valueLabel = null;

            // Track the best-value-seen and the candidate leader. Compare floats / ints
            // separately to avoid lossy casts at the boundary.
            bool haveCandidate = false;
            ulong candidate = 0;
            int candidateJoinIndex = int.MaxValue;
            float bestFloat = 0f;
            int bestInt = 0;

            for (int i = 0; i < states.Count; i++)
            {
                CourseManager.PlayerState s = states[i];
                if (s.isSpectator || !s.isConnected) continue;
                if (!IsEligible(s, metric)) continue;

                bool better;
                if (!haveCandidate)
                {
                    better = true;
                }
                else if (UsesIntComparison(metric))
                {
                    int v = ReadInt(s, metric);
                    better = LowerIsBetter(metric) ? (v < bestInt) : (v > bestInt);
                }
                else
                {
                    float v = ReadFloat(s, metric);
                    better = LowerIsBetter(metric) ? (v < bestFloat) : (v > bestFloat);
                }

                if (better)
                {
                    candidate = s.playerGuid;
                    candidateJoinIndex = s.joinIndex;
                    haveCandidate = true;
                    if (UsesIntComparison(metric)) bestInt = ReadInt(s, metric);
                    else                           bestFloat = ReadFloat(s, metric);
                }
                else if (HasTie(s, metric, bestInt, bestFloat) && s.joinIndex < candidateJoinIndex)
                {
                    // Stable tie-break by joinIndex — same player wins ties across reconciles
                    // so we don't ping-pong announcements when two players hit the same value.
                    candidate = s.playerGuid;
                    candidateJoinIndex = s.joinIndex;
                }
            }

            if (!haveCandidate)
                return false;

            leaderGuid = candidate;
            valueLabel = FormatValue(states, candidate, metric, bestInt, bestFloat);
            return true;
        }

        private static bool IsEligible(CourseManager.PlayerState s, Metric metric)
        {
            switch (metric)
            {
                case Metric.BestHoleScore: return s.bestHoleScore > StrokesUnderParType.None;
                case Metric.LongestChipIn: return s.longestChipIn > float.MinValue;
                case Metric.AvgFinishTime: return s.avgFinishTime > 0f && s.finishes > 0;
                case Metric.ItemPickups:   return s.itemPickups > 0;
                case Metric.KORatio:       return s.matchKnockouts > 0;
                default: return false;
            }
        }

        private static bool UsesIntComparison(Metric m)
        {
            return m == Metric.BestHoleScore || m == Metric.ItemPickups;
        }

        private static bool LowerIsBetter(Metric m)
        {
            return m == Metric.AvgFinishTime;
        }

        private static int ReadInt(CourseManager.PlayerState s, Metric m)
        {
            switch (m)
            {
                case Metric.BestHoleScore: return (int)s.bestHoleScore;
                case Metric.ItemPickups:   return s.itemPickups;
                default: return 0;
            }
        }

        private static float ReadFloat(CourseManager.PlayerState s, Metric m)
        {
            switch (m)
            {
                case Metric.LongestChipIn: return s.longestChipIn;
                case Metric.AvgFinishTime: return s.avgFinishTime;
                case Metric.KORatio:       return ComputeKORatio(s);
                default: return 0f;
            }
        }

        private static float ComputeKORatio(CourseManager.PlayerState s)
        {
            // Standard FPS-style ratio: KOs delivered ÷ max(1, KOs received). Players with
            // zero deaths get raw KO count as their ratio, which is the right behaviour —
            // never-died-and-knocked-out-five is a stronger story than 5/1 = 5.0.
            int denom = Mathf.Max(1, s.matchKnockedOut);
            return (float)s.matchKnockouts / denom;
        }

        private static bool HasTie(CourseManager.PlayerState s, Metric metric, int bestInt, float bestFloat)
        {
            if (UsesIntComparison(metric))
                return ReadInt(s, metric) == bestInt;
            return Mathf.Approximately(ReadFloat(s, metric), bestFloat);
        }

        private static string FormatValue(SyncList<CourseManager.PlayerState> states, ulong leaderGuid, Metric metric, int bestInt, float bestFloat)
        {
            switch (metric)
            {
                case Metric.BestHoleScore:
                    return DescribeStrokesUnderPar((StrokesUnderParType)bestInt);
                case Metric.LongestChipIn:
                    return $"{Mathf.RoundToInt(bestFloat)}m";
                case Metric.AvgFinishTime:
                    return $"{bestFloat:0.0}s";
                case Metric.ItemPickups:
                    return bestInt.ToString();
                case Metric.KORatio:
                {
                    // Reach back into the leader's state for the (KOs/deaths) breakdown so
                    // the chat line carries both the ratio and the raw counts.
                    for (int i = 0; i < states.Count; i++)
                    {
                        if (states[i].playerGuid != leaderGuid) continue;
                        var s = states[i];
                        return $"{bestFloat:0.00} ({s.matchKnockouts}/{s.matchKnockedOut})";
                    }
                    return bestFloat.ToString("0.00");
                }
                default: return string.Empty;
            }
        }

        private static string MetricLabel(Metric m)
        {
            switch (m)
            {
                case Metric.BestHoleScore: return "best hole";
                case Metric.LongestChipIn: return "longest chip-in";
                case Metric.AvgFinishTime: return "average finish time";
                case Metric.ItemPickups:   return "item pickups";
                case Metric.KORatio:       return "K/O ratio";
                default: return "stat";
            }
        }

        private static string DescribeStrokesUnderPar(StrokesUnderParType type)
        {
            switch (type)
            {
                case StrokesUnderParType.HoleInOne: return "Hole in One";
                case StrokesUnderParType.Condor:    return "Condor";
                case StrokesUnderParType.Albatross: return "Albatross";
                case StrokesUnderParType.Eagle:     return "Eagle";
                case StrokesUnderParType.Birdie:    return "Birdie";
                case StrokesUnderParType.Par:       return "Par";
                default: return "—";
            }
        }
    }

    internal static class Narrator
    {
        // Stand-alone bracketed prefix so the line reads as system commentary rather than
        // impersonating a player.
        private const string Prefix = "<color=#9aa6ff><b>[Match]</b></color> ";

        internal static void Post(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;
            try
            {
                TextChatUi.ShowMessage(Prefix + message);
            }
            catch
            {
                Plugin.Log?.LogInfo($"[chat-fallback] {message}");
            }
        }

        internal static string PlayerNameFromGuid(ulong guid)
        {
            PlayerInfo local = GameManager.LocalPlayerInfo;
            if (local != null && local.PlayerId != null && local.PlayerId.Guid == guid)
                return local.PlayerId.PlayerNameNoRichText;
            if (GameManager.RemotePlayers != null)
            {
                foreach (PlayerInfo remote in GameManager.RemotePlayers)
                {
                    if (remote == null || remote.PlayerId == null) continue;
                    if (remote.PlayerId.Guid == guid)
                        return remote.PlayerId.PlayerNameNoRichText;
                }
            }
            return "Someone";
        }

        internal static string DescribeStrokesUnderPar(StrokesUnderParType type)
        {
            switch (type)
            {
                case StrokesUnderParType.HoleInOne: return "scored a hole in one!";
                case StrokesUnderParType.Albatross: return "landed an albatross!";
                case StrokesUnderParType.Eagle:     return "landed an eagle!";
                case StrokesUnderParType.Birdie:    return "landed a birdie!";
                case StrokesUnderParType.Condor:    return "pulled off a condor!";
                default: return null;
            }
        }
    }

    // Per-hole event notifications retained: hole-in-one / albatross / eagle / birdie / condor.
    [HarmonyPatch(typeof(InfoFeed), "UserCode_RpcShowMessage__StrokesMessageData")]
    internal static class Patch_StrokesMessage
    {
        private static void Postfix(InfoFeed.StrokesMessageData messageData)
        {
            string verb = Narrator.DescribeStrokesUnderPar(messageData.strokesUnderParType);
            if (verb == null)
                return; // Skip Par — default outcome line.
            string player = Narrator.PlayerNameFromGuid(messageData.playerGuid);
            Narrator.Post($"{player} {verb}");
        }
    }

    // Speedrun retained — distinct hole-event the user explicitly called out.
    [HarmonyPatch(typeof(InfoFeed), "UserCode_RpcShowMessage__SpeedrunMessageData")]
    internal static class Patch_SpeedrunMessage
    {
        private static void Postfix(InfoFeed.SpeedrunMessageData messageData)
        {
            string player = Narrator.PlayerNameFromGuid(messageData.playerGuid);
            Narrator.Post($"{player} sprinted through the hole — that was fast!");
        }
    }

    // 0.3.0 also posted per-shot chip-in / per-knockout / nice-shot lines and pairwise
    // score/knockout/strokes overtakes. Per the 0.4.0 brief those are noise — chip-in
    // brags now surface only when someone takes first place on the longestChipIn
    // leaderboard, knockouts surface as K/O-ratio leadership changes, and the perfect-
    // drive nice-shot VFX hook is gone entirely.
}
