// Developer test tooling: compiled into Debug builds only, never into a release (the release
// workflows build Release). See docs/netplay/05-local-testing.md.
#if DEBUG
using Assets.Scripts.Actors.Player;
using OmniBonk.Configuration;
using UnityEngine;

namespace OmniBonk.Scripts.Bot
{
    /// <summary>
    /// Test only (Duo Shrines): both bots charge one charge shrine together. The host walks to the
    /// nearest unfinished one and stands in it until it completes; a client walks into the shrine its
    /// host is standing at. Without this the two only share a zone by chance.
    /// </summary>
    public partial class BotController
    {
        private const float SHARE_SHRINE_HOST_RANGE = 250f;
        private const float SHARE_SHRINE_OWNER_RANGE = 22f;
        private const float SHARE_SHRINE_STAND_RADIUS = 1.5f;
        private const float SHARE_SHRINE_GIVE_UP_SECONDS = 90f;

        private const float SHARE_SHRINE_TELEPORT_SECONDS = 8f;

        private const float SHARE_SHRINE_STAGING_DISTANCE = 18f;
        private const float SHARE_SHRINE_WAIT_SECONDS = 45f;
        private const float SHARE_SHRINE_TEAMMATE_RADIUS = 6f;

        private ChargeShrine shareShrine;
        private float shareShrineSince;
        private float lastShareTeleport;
        private bool shareShrineEntered;

        private bool TeammateInShrine(Vector3 center)
        {
            foreach (var netPlayer in playerManagerService.GetAllSpawnedNetPlayers())
            {
                var model = netPlayer != null ? netPlayer.Model : null;
                if (model == null)
                {
                    continue;
                }

                var flat = model.transform.position - center;
                flat.y = 0f;
                if (flat.magnitude <= SHARE_SHRINE_TEAMMATE_RADIUS)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when it steered this tick, so the caller skips its own movement.</summary>
        private bool TickShareShrine(MyPlayer me, Vector3 owner)
        {
            if (!ModConfig.TestShareShrines.Value)
            {
                return false;
            }

            if (shareShrine != null && (shareShrine.rewardGiven || Time.time - shareShrineSince > SHARE_SHRINE_GIVE_UP_SECONDS))
            {
                Plugin.Log.LogInfo($"[bot] left shrine {shareShrine.gameObject.name} after {Time.time - shareShrineSince:F0} s (done: {shareShrine.rewardGiven}).");
                BotTargets.Blacklist(shareShrine);
                shareShrine = null;
            }

            if (shareShrine == null)
            {
                // The host picks from its own position; a client only from where its host stands.
                var center = IsAutoHost ? me.transform.position : owner;
                var range = IsAutoHost ? SHARE_SHRINE_HOST_RANGE : SHARE_SHRINE_OWNER_RANGE;
                shareShrine = BotTargets.FindChargeShrine(center, range, detect);
                if (shareShrine == null)
                {
                    return false;
                }

                shareShrineSince = Time.time;
                shareShrineEntered = false;
                DropTarget();
                Plugin.Log.LogInfo($"[bot] heading to shrine {shareShrine.gameObject.name} to charge it together.");
                lastShareTeleport = Time.time;
            }

            var target = shareShrine.transform.position;

            // A shrine charges in about 5 s, less than a client takes to catch up, so the host waits at a
            // staging spot outside the zone (wider than 7 m: a host staged at 7 m charged it alone) until a teammate is inside (the client walks in once its host is
            // within SHARE_SHRINE_OWNER_RANGE of a shrine), then steps in itself.
            if (IsAutoHost && !shareShrineEntered)
            {
                if (TeammateInShrine(target) || Time.time - shareShrineSince > SHARE_SHRINE_WAIT_SECONDS)
                {
                    shareShrineEntered = true;
                    Plugin.Log.LogInfo($"[bot] stepping into shrine {shareShrine.gameObject.name} (teammate inside: {TeammateInShrine(target)}).");
                    TeleportIntoShrine(me, shareShrine);
                    return true;
                }

                var staging = target + new Vector3(SHARE_SHRINE_STAGING_DISTANCE, 0f, 0f);
                var toStaging = staging - me.transform.position;
                toStaging.y = 0f;
                if (toStaging.magnitude > 2f && Time.time - lastShareTeleport > 2f)
                {
                    lastShareTeleport = Time.time;
                    var spot = GroundUnder(staging, target, 3f);
                    me.transform.position = spot;
                    var stagingBody = me.playerMovement.rb;
                    if (stagingBody != null)
                    {
                        stagingBody.position = spot;
                        stagingBody.velocity = Vector3.zero;
                    }
                }

                BotInput.Move = Vector2.zero;
                return true;
            }

            var flat = target - me.transform.position;
            flat.y = 0f;
            if (flat.magnitude <= SHARE_SHRINE_STAND_RADIUS)
            {
                BotInput.Move = Vector2.zero;
                return true;
            }

            // The host's stuck recovery teleports to its owner, which is itself, so terrain between it and
            // the shrine stranded it (90 s, never in the zone). Not at pick time: at stage start the game
            // then moves the player to its spawn. A client that gets stuck lands on its host, in the shrine.
            if (IsAutoHost && Time.time - lastShareTeleport > SHARE_SHRINE_TELEPORT_SECONDS)
            {
                lastShareTeleport = Time.time;
                TeleportIntoShrine(me, shareShrine);
                return true;
            }

            SteerTo(me, target, IsAutoHost ? me.transform.position : owner);
            return true;
        }

        private void TeleportIntoShrine(MyPlayer me, ChargeShrine shrine)
        {
            var center = shrine.transform.position;
            var spot = GroundUnder(center, center, 3f);
            me.transform.position = spot;
            var body = me.playerMovement.rb;
            if (body != null)
            {
                body.position = spot;
                body.velocity = Vector3.zero;
            }

            Teleports++;
            Plugin.Log.LogInfo($"[bot] teleported into shrine {shrine.gameObject.name} (TestShareShrines).");
        }
    }
}
#endif
