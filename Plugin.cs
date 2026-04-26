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
        public const string ModVersion = "0.3.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            new Harmony(ModGuid).PatchAll();
            StatPassTracker.Hook();
            Log.LogInfo($"{ModName} v{ModVersion} loaded.");
        }

        private void OnDestroy()
        {
            StatPassTracker.Unhook();
        }
    }

    // Watches CourseManager.PlayerStates and posts a chat line whenever one player overtakes
    // another on a tracked metric (score / knockouts / strokes). Pure local renderer per
    // modded client — no Mirror traffic, every modded peer reads the same SyncList state.
    internal static class StatPassTracker
    {
        private enum Metric { Score, Knockouts, Strokes }

        // For Score / Knockouts higher is better; for Strokes lower is better. The
        // "passed" trigger is: was strictly worse last sample → now better-or-equal.
        private static readonly Metric[] AllMetrics = { Metric.Score, Metric.Knockouts, Metric.Strokes };
        private static readonly Dictionary<long, sbyte> PreviousSign = new Dictionary<long, sbyte>();
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
            PreviousSign.Clear();
            subscribed = false;
        }

        private static void OnPlayerStatesChanged(SyncList<CourseManager.PlayerState>.Operation op, int index, CourseManager.PlayerState changed)
        {
            // Walk the full list each time and reconcile pairwise comparisons. SyncList
            // changes fire frequently during scoring so we keep this small-N (lobbies are
            // capped well under a dozen) and rebuild the sign dictionary each tick.
            SyncList<CourseManager.PlayerState> states = CourseManager.PlayerStates;
            if (states == null || states.Count < 2) return;

            // First pass: snapshot. Then post messages for transitions.
            int n = states.Count;
            for (int i = 0; i < n; i++)
            {
                CourseManager.PlayerState a = states[i];
                if (a.isSpectator) continue;
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    CourseManager.PlayerState b = states[j];
                    if (b.isSpectator) continue;

                    foreach (Metric metric in AllMetrics)
                    {
                        int aVal = ReadMetric(a, metric);
                        int bVal = ReadMetric(b, metric);
                        sbyte newSign = CompareForBetter(aVal, bVal, metric);

                        long key = MakeKey(a.playerGuid, b.playerGuid, metric);
                        sbyte oldSign = PreviousSign.TryGetValue(key, out sbyte stored) ? stored : (sbyte)0;
                        PreviousSign[key] = newSign;

                        // Trigger only on the rising edge: a was strictly worse (-1) and is
                        // now tied-or-better (>=0). This avoids posting on the initial seed
                        // (oldSign defaults to 0) and on tie oscillations.
                        if (oldSign == -1 && newSign >= 0)
                        {
                            string passer = Narrator.PlayerNameFromGuid(a.playerGuid);
                            string passed = Narrator.PlayerNameFromGuid(b.playerGuid);
                            Narrator.Post($"{passer} passed {passed} on {MetricLabel(metric)}.");
                        }
                    }
                }
            }
        }

        private static int ReadMetric(CourseManager.PlayerState s, Metric m)
        {
            switch (m)
            {
                case Metric.Score: return s.matchScore;
                case Metric.Knockouts: return s.matchKnockouts;
                case Metric.Strokes: return s.matchStrokes;
                default: return 0;
            }
        }

        private static sbyte CompareForBetter(int a, int b, Metric m)
        {
            // For Score and Knockouts, higher is better. For Strokes, lower is better.
            int signed = (m == Metric.Strokes) ? b - a : a - b;
            if (signed > 0) return 1;
            if (signed < 0) return -1;
            return 0;
        }

        private static long MakeKey(ulong a, ulong b, Metric m)
        {
            // Pair-key into a 64-bit hash. Lobbies are tiny (<= 8 players) so this just needs
            // to be deterministic and avoid collisions for a single session.
            unchecked
            {
                long h = (long)((a * 1099511628211UL) ^ b);
                return (h << 2) | (long)m;
            }
        }

        private static string MetricLabel(Metric m)
        {
            switch (m)
            {
                case Metric.Score: return "score";
                case Metric.Knockouts: return "knockouts";
                case Metric.Strokes: return "putts";
                default: return "stat";
            }
        }
    }

    internal static class Narrator
    {
        // Narrator prefix mimics a non-player chat author. Vanilla chat messages are formatted
        // as "[playerName]: [message]" — we go with a stand-alone bracketed prefix so the
        // notification reads as system / commentary rather than impersonating a player.
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
                // TextChatUi singleton may not be initialised yet on some scene loads. Fall back
                // to logging so the event isn't silently lost.
                Plugin.Log?.LogInfo($"[chat-fallback] {message}");
            }
        }

        internal static string PlayerNameFromGuid(ulong guid)
        {
            // Local player first, then remote — small lobbies, linear scan is fine.
            PlayerInfo local = GameManager.LocalPlayerInfo;
            if (local != null && local.PlayerId != null && local.PlayerId.Guid == guid)
            {
                return local.PlayerId.PlayerNameNoRichText;
            }
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
                case StrokesUnderParType.Eagle: return "landed an eagle!";
                case StrokesUnderParType.Birdie: return "landed a birdie!";
                case StrokesUnderParType.Condor: return "pulled off a condor!";
                default: return null;
            }
        }
    }

    // SwingNiceShot is replicated to every client via VfxManager.ServerPlayPooledVfxForAllClients,
    // which means the nice-shot visual fires globally — including on the driving range, where
    // InfoFeed RPCs never run because there's no hole-completion event. Hooking here gives us
    // per-shot chat coverage everywhere a perfect-swing visual lands.
    internal static class NiceShotChatHook
    {
        private const float DuplicateDebounceSeconds = 0.5f;
        private const float ProximityMatchSquared = 9f * 9f; // 9 m max from VFX origin to player

        private static float lastPostTime;
        private static Vector3 lastPostPosition;

        internal static void OnSwingNiceShot(Vector3 position)
        {
            // Multi-hit perfect swings fire the VFX several times in the same frame; debounce
            // by both time and position so a single shot only posts a single chat line.
            float now = Time.unscaledTime;
            if (now - lastPostTime < DuplicateDebounceSeconds &&
                (position - lastPostPosition).sqrMagnitude < 1f)
            {
                return;
            }
            lastPostTime = now;
            lastPostPosition = position;

            string playerName = ResolveNearestPlayerName(position);
            Narrator.Post($"{playerName} hit a perfect drive!");
        }

        private static string ResolveNearestPlayerName(Vector3 position)
        {
            PlayerInfo best = null;
            float bestSqr = ProximityMatchSquared;

            PlayerInfo local = GameManager.LocalPlayerInfo;
            if (local != null && local.transform != null)
            {
                float sqr = (local.transform.position - position).sqrMagnitude;
                if (sqr <= bestSqr) { best = local; bestSqr = sqr; }
            }
            if (GameManager.RemotePlayers != null)
            {
                foreach (PlayerInfo remote in GameManager.RemotePlayers)
                {
                    if (remote == null || remote.transform == null) continue;
                    float sqr = (remote.transform.position - position).sqrMagnitude;
                    if (sqr < bestSqr) { best = remote; bestSqr = sqr; }
                }
            }
            if (best == null || best.PlayerId == null)
                return "Someone";
            return best.PlayerId.PlayerNameNoRichText;
        }
    }

    [HarmonyPatch(typeof(VfxManager), "PlayPooledVfxLocalOnlyInternal",
        new[] { typeof(VfxType), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(uint), typeof(bool), typeof(float), typeof(Action<PoolableParticleSystem>) })]
    internal static class Patch_VfxManager_NiceShot
    {
        private static void Postfix(VfxType vfxType, Vector3 position)
        {
            if (vfxType != VfxType.SwingNiceShot) return;
            NiceShotChatHook.OnSwingNiceShot(position);
        }
    }

    [HarmonyPatch(typeof(InfoFeed), "UserCode_RpcShowMessage__StrokesMessageData")]
    internal static class Patch_StrokesMessage
    {
        private static void Postfix(InfoFeed.StrokesMessageData messageData)
        {
            string verb = Narrator.DescribeStrokesUnderPar(messageData.strokesUnderParType);
            if (verb == null)
                return; // Skip Par — it's the default "expected outcome" line and would be noisy.
            string player = Narrator.PlayerNameFromGuid(messageData.playerGuid);
            Narrator.Post($"{player} {verb}");
        }
    }

    [HarmonyPatch(typeof(InfoFeed), "UserCode_RpcShowMessage__ChipInMessageData")]
    internal static class Patch_ChipInMessage
    {
        private static void Postfix(InfoFeed.ChipInMessageData messageData)
        {
            string player = Narrator.PlayerNameFromGuid(messageData.playerGuid);
            Narrator.Post($"{player} chipped in from {Mathf.RoundToInt(messageData.distance)}m!");
        }
    }

    [HarmonyPatch(typeof(InfoFeed), "UserCode_RpcShowMessage__SpeedrunMessageData")]
    internal static class Patch_SpeedrunMessage
    {
        private static void Postfix(InfoFeed.SpeedrunMessageData messageData)
        {
            string player = Narrator.PlayerNameFromGuid(messageData.playerGuid);
            Narrator.Post($"{player} sprinted through the hole — that was fast!");
        }
    }

    [HarmonyPatch(typeof(InfoFeed), "UserCode_RpcShowMessage__KnockoutMessageData")]
    internal static class Patch_KnockoutMessage
    {
        private static void Postfix(InfoFeed.KnockoutMessageData messageData)
        {
            string responsible = Narrator.PlayerNameFromGuid(messageData.responsiblePlayer);
            string victim = Narrator.PlayerNameFromGuid(messageData.knockedOutPlayer);
            if (responsible == victim)
                return; // self-knockouts have their own dedicated message data type.
            Narrator.Post($"{responsible} knocked out {victim}.");
        }
    }
}
