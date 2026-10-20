using Assets.Scripts.Inventory__Items__Pickups.Items;
using UnityEngine;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Endless Run, Phase 3: puts an arrival in the Graveyard scene into the room the route chose.
    /// Stateless; <see cref="Services.EndlessRunService"/> says when and which room.
    ///
    /// <para><b>The vanilla Graveyard</b> (decompiled, build 21750826) is one stage, played in order:
    /// the first crypt, then <c>InteractableCryptLeave.Teleport</c> (0x1804C8A20) stops the dungeon
    /// and teleports to the surface; the key door's <c>InteractableCrypt.Teleport</c> (0x1804C90E0)
    /// generates the second crypt with <c>RsgController.Generate(mapSeed + 1, BossDungeon)</c>,
    /// points that crypt's exit at the boss room and starts the dungeon; that exit then activates
    /// <c>GraveyardBossRoom</c>; and <c>InteractableGhostBossLeave.Teleport</c> (0x1804CB5B0) clears
    /// the map. Each room here repeats the part of that sequence that leads into it, at the
    /// spawn portal, so the player arrives there.</para>
    ///
    /// <para><b>Every peer runs this,</b> in the same order: the crypt comes from the session seed
    /// (<c>RsgController.SetCustomSeed</c>), so every peer generates the same second crypt, and the
    /// host registers its objects as it does for the key door. The boss room's lamps and exit get
    /// their ids in lockstep in <c>GraveyardBossRoomPatches.Activate_Prefix</c>, which every peer
    /// reaches once. <b>UNVERIFIED, not run in-game.</b></para>
    /// </summary>
    internal static class EndlessGraveyard
    {
        /// <summary>At the spawn portal, before the player appears.</summary>
        internal static void Prepare(SpawnPlayerPortal portal, EndlessRoom room)
        {
            var rsg = RsgController.Instance;
            var game = GameManager.Instance;
            if (portal == null || rsg == null || game == null || room == EndlessRoom.Crypt1)
            {
                return; // The scene already starts in the first crypt.
            }

            var position = portal.transform.position;
            var direction = portal.transform.forward;

            switch (room)
            {
                case EndlessRoom.Crypt2:
                {
                    rsg.ClearMap();
                    rsg.Generate(MapGenerationController.mapSeed + 1, RsgController.EDungeonType.BossDungeon, out var traversalTime);
                    game.StartDungeon(traversalTime);
                    game.cryptIndex = 1;

                    var start = rsg.rsgStart.spawnTransform;
                    position = start.position;
                    direction = start.forward;

                    var bossArrival = rsg.roomBoss.playerTeleportTransform;
                    rsg.rsgEnd.SetTeleportTransform(bossArrival.position, bossArrival.forward, bossArrival.forward);
                    break;
                }
                case EndlessRoom.Graveyard:
                    // Where the first crypt's exit would have put us, read before the crypt is cleared.
                    position = rsg.rsgEnd.teleportPosition;
                    direction = rsg.rsgEnd.teleportDir;
                    game.StopDungeon();
                    rsg.ClearMap();
                    break;
                case EndlessRoom.GhostKing:
                {
                    var bossArrival = rsg.roomBoss.playerTeleportTransform;
                    position = bossArrival.position;
                    direction = bossArrival.forward;
                    game.StopDungeon();
                    rsg.ClearMap();
                    rsg.roomBoss.gameObject.SetActive(true);
                    Physics.SyncTransforms();
                    break;
                }
                default:
                    return;
            }

            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }

            portal.transform.SetPositionAndRotation(position, Quaternion.LookRotation(direction));
            Plugin.Log.LogInfo($"[endless] Graveyard arrival moved into {room} at {position}.");
        }

        /// <summary>Once the run is playing in the room.</summary>
        internal static void Activate(EndlessRoom room)
        {
            if (room == EndlessRoom.Graveyard)
            {
                // Keys left from an earlier Graveyard visit would open the door at once.
                var items = GameManager.Instance?.player?.inventory?.itemInventory;
                while (items != null && items.GetAmount(EItem.CryptKey) > 0)
                {
                    items.RemoveItem(EItem.CryptKey, false);
                }

                UiManager.Instance?.objective?.GraveyardKeys();
            }
            else if (room == EndlessRoom.GhostKing)
            {
                RsgController.Instance?.roomBoss?.Activate();
            }
        }
    }
}
