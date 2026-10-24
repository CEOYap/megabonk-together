// Developer test tooling: compiled into Debug builds only, never into a release (the release
// workflows build Release). See docs/netplay/05-local-testing.md.
#if DEBUG
using System.Linq;
using Assets.Scripts._Data.MapsAndStages;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using OmniBonk.Configuration;
using UnityEngine;

namespace OmniBonk.Scripts.Bot
{
    /// <summary>
    /// The auto-host half of the bot: hosts a lobby, starts the run once every BotMode copy is in and
    /// ready, wanders the stage, and takes the stage's portal after
    /// <see cref="ModConfig.AutoHostStageMinutes"/>. Everything goes through the same paths a player's
    /// clicks and key presses use, so the session replicates as it would by hand.
    /// </summary>
    public partial class BotController
    {
        private static bool IsAutoHost => ModConfig.AutoHost.Value;

        /// <summary>A wander point this far from the host's current position, re-picked on arrival.</summary>
        private const float WANDER_MIN_DISTANCE = 12f;
        private const float WANDER_MAX_DISTANCE = 30f;

        /// <summary>A wander point not reached in this time is abandoned for a new one.</summary>
        private const float WANDER_POINT_SECONDS = 10f;

        /// <summary>Seconds without getting closer before a new point is picked, ahead of the bot's stuck teleport.</summary>
        private const float WANDER_STALL_SECONDS = 3f;

        private float nextStartRequestTime;
        private float nextMapConfirmTime;
        private Vector3? wanderPoint;
        private float wanderPointTime;
        private float stageStartTime = -1f;
        private int stagesAdvanced;
        private InteractablePortal leavingThrough;
        private bool pressedPortal;
        private bool loggedNoPortal;
        private bool mapChosen;
        private int mapSelectTries;

        private void ResetAutoHost()
        {
            mapChosen = false;
            mapSelectTries = 0;
            wanderPoint = null;
            stageStartTime = -1f;
            stagesAdvanced = 0;
            leavingThrough = null;
            pressedPortal = false;
            loggedNoPortal = false;
        }

        /// <summary>Between stages: the next stage's clock starts when the host has a player again.</summary>
        private void OnStageGone()
        {
            wanderPoint = null;
            stageStartTime = -1f;
            leavingThrough = null;
            pressedPortal = false;
            loggedNoPortal = false;
        }

        /// <summary>From a settled main menu, opens a lobby. The matchmaker's room code reaches BotMode copies through the room-code file.</summary>
        private void TryHost()
        {
            if (Time.unscaledTime < nextJoinCheckTime)
            {
                return;
            }

            nextJoinCheckTime = Time.unscaledTime + 1f;

            if (WindowManager.activeWindow == null || WindowManager.activeWindow.name != "Menu")
            {
                menuSince = -1f;
                return;
            }

            if (menuSince < 0f)
            {
                menuSince = Time.unscaledTime;
            }

            if (Time.unscaledTime - menuSince < MENU_SETTLE_SECONDS
                || netplaySessionService.IsBusy
                || synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            var playTogether = Plugin.Instance.PlayTogetherButton;
            if (playTogether == null || Modal.LobbyPanel.Current != null)
            {
                return;
            }

            State = BotState.Joining;
            joinStartTime = Time.unscaledTime;
            Plugin.Log.LogInfo($"[bot] hosting a lobby for {ModConfig.AutoHostPlayers.Value} player(s).");
            playTogether.OpenLobby();
        }

        /// <summary>
        /// The host's two lobby steps a bot never takes: Start once everyone is in and ready, and the
        /// map screen's confirm. True when it acted, so the shared ready and character steps wait.
        /// </summary>
        private bool TickHostLobby()
        {
            var now = Time.unscaledTime;

            if (Modal.LobbyPanel.Current != null
                && lobbyViewService.IsLocalPlayerReady
                && lobbyViewService.AreAllMembersReady
                && playerManagerService.GetAllPlayers().Count() >= ModConfig.AutoHostPlayers.Value)
            {
                if (now >= nextStartRequestTime)
                {
                    nextStartRequestTime = now + 3f;
                    Plugin.Log.LogInfo("[bot] everyone is ready; starting.");
                    lobbyViewService.RequestStart();
                }

                return true;
            }

            // Its confirm stays inactive until every peer is ready (WindowManagerPatches); press until it takes.
            if (WindowManager.activeWindow != null && WindowManager.activeWindow.name == "Maps And Stats")
            {
                var mapSelection = Plugin.Instance.GetMainMenu()?.mapSelectionUi;
                if (!mapChosen && mapSelection != null)
                {
                    mapChosen = TrySelectMap(mapSelection, ++mapSelectTries >= 10);
                    nextMapConfirmTime = now + 2f; // let the selection settle before confirming
                    return true;
                }

                var confirm = mapSelection?.btnConfirm;
                if (confirm != null && confirm.button != null && now >= nextMapConfirmTime)
                {
                    nextMapConfirmTime = now + 2f;
                    confirm.button.onClick.Invoke();
                    Plugin.Log.LogInfo("[bot] confirmed the map.");
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Clicks the map entry for <see cref="ModConfig.AutoHostMap"/> through its own button, as a
        /// player would. True when done or nothing to do; false to retry while the entries build.
        /// </summary>
        private static bool TrySelectMap(MapSelectionUi mapSelection, bool lastTry)
        {
            var wanted = ModConfig.AutoHostMap.Value;
            if (string.IsNullOrWhiteSpace(wanted))
            {
                return true;
            }

            if (!System.Enum.TryParse<EMap>(wanted.Trim(), true, out var map))
            {
                Plugin.Log.LogWarning($"[bot] AutoHostMap '{wanted}' is not a map name; keeping the selected map.");
                return true;
            }

            // Decompiled OnMapSelected (0x180570590) reads the map from the button's MapEntry. Inactive
            // entries included. The generic GetComponentsInChildren<T>(bool) does not bind under IL2CPP.
            var entries = Helpers.Il2CppFindHelper.RuntimeGetComponentsInChildren<MapEntry>(mapSelection.gameObject, true);
            var seen = new System.Collections.Generic.List<string>();
            foreach (var entry in entries)
            {
                var entryMap = entry != null && entry.mapData != null ? entry.mapData.eMap : EMap.None;
                seen.Add(entryMap.ToString());
                if (entryMap != map)
                {
                    continue;
                }

                var button = entry.buttonScript;
                if (button == null || !button.canSelect || button.button == null || (entry.locked != null && entry.locked.activeSelf))
                {
                    Plugin.Log.LogWarning($"[bot] map {map} cannot be selected (locked?); keeping the selected map.");
                    return true;
                }

                button.button.onClick.Invoke();
                Plugin.Log.LogInfo($"[bot] selected the {map} map.");
                return true;
            }

            if (lastTry)
            {
                Plugin.Log.LogWarning($"[bot] no {map} entry on the map screen (saw: {(seen.Count == 0 ? "none" : string.Join(", ", seen))}); keeping the selected map.");
            }

            return lastTry;
        }

        /// <summary>In a run: leave through the portal once the stage time is up, otherwise wander and use what is near.</summary>
        private void TickHostRun(MyPlayer me)
        {
            var position = me.transform.position;
            OwnerPosition = position;

            if (stageStartTime < 0f)
            {
                stageStartTime = Time.time;
                Plugin.Log.LogInfo($"[bot] on stage {stagesAdvanced + 1}.");
            }

            var stageMinutes = ModConfig.AutoHostStageMinutes.Value;
            if (stageMinutes > 0f && Time.time - stageStartTime >= stageMinutes * 60f && TickLeaveStage(me))
            {
                return;
            }

            var detour = FindDetourSafely(position, position);
            if (detour != null)
            {
                detourTarget = detour;
                detourStartTime = Time.time;
                State = BotState.Detouring;
                DropTarget();
                return;
            }

            if (!wanderPoint.HasValue
                || Vector3.Distance(position, wanderPoint.Value) < 2f
                || Time.time - wanderPointTime > WANDER_POINT_SECONDS
                || Time.time - lastProgressTime > WANDER_STALL_SECONDS)
            {
                wanderPoint = PickWanderPoint(position);
                wanderPointTime = Time.time;
                DropTarget();
            }

            SteerTo(me, wanderPoint.Value, position);
        }

        /// <summary>
        /// A point in open line of sight: each try's distance is cut short of the first wall a ray
        /// hits, so the host does not walk into one (crypt rooms are mazes). Falls back to the longest.
        /// </summary>
        private static Vector3 PickWanderPoint(Vector3 position)
        {
            var origin = position + Vector3.up;
            var best = position;
            var bestDistance = 0f;
            for (var i = 0; i < 8; i++)
            {
                var flat = Random.insideUnitCircle.normalized;
                var direction = new Vector3(flat.x, 0f, flat.y);
                var distance = Random.Range(WANDER_MIN_DISTANCE, WANDER_MAX_DISTANCE);
                if (Physics.Raycast(origin, direction, out var hit, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    distance = hit.distance - 2f;
                }

                if (distance >= WANDER_MIN_DISTANCE * 0.5f)
                {
                    return position + direction * distance;
                }

                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    best = position + direction * distance;
                }
            }

            return best;
        }

        /// <summary>
        /// Opens the stage's portal (the game opens it when the boss dies) and walks to it; a stall
        /// teleports onto it. The press goes through <see cref="BotInput"/>, so the mod's normal
        /// interact path syncs the stage change. False when this stage has no portal.
        /// </summary>
        private bool TickLeaveStage(MyPlayer me)
        {
            if (leavingThrough == null)
            {
                // Not every stage's spawner is registered with the spawned-object service on the host.
                var spawner = spawnedObjectManagerService.GetSpecific<InteractableBossSpawner>()
                    ?? Object.FindObjectOfType<InteractableBossSpawner>();
                if (spawner == null || spawner.portal == null)
                {
                    if (!loggedNoPortal)
                    {
                        loggedNoPortal = true;
                        Plugin.Log.LogInfo("[bot] stage time up, but this stage has no boss portal (a crypt or Endless room?); staying.");
                    }

                    return false;
                }

                if (!spawner.portal.activeSelf)
                {
                    spawner.portal.SetActive(true);
                }

                leavingThrough = spawner.portal.GetComponent<InteractablePortal>();
                if (leavingThrough == null)
                {
                    return false;
                }

                pressedPortal = false;
                DropTarget();
                Plugin.Log.LogInfo($"[bot] stage time up after {ModConfig.AutoHostStageMinutes.Value:0.#} min; heading to the portal.");
            }

            if (!pressedPortal && IsCurrentInteractable(leavingThrough))
            {
                BotInput.PressInteract(leavingThrough.GetInstanceID());
                BotInput.Move = Vector2.zero;
                pressedPortal = true;
                stagesAdvanced++;
                Plugin.Log.LogInfo($"[bot] entering the portal (stage change {stagesAdvanced}).");
                return true;
            }

            var portalPosition = leavingThrough.transform.position;
            SteerTo(me, portalPosition, portalPosition);
            return true;
        }
    }
}
#endif
