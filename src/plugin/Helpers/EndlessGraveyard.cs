using Assets.Scripts.Inventory__Items__Pickups.Items;
using UnityEngine;

namespace OmniBonk.Helpers
{
    /// <summary>
    /// Endless: places a Graveyard arrival into the chosen room by replaying the vanilla sequence that
    /// leads there (decompiled: <c>InteractableCryptLeave.Teleport</c> 0x1804C8A20,
    /// <c>InteractableCrypt.Teleport</c> 0x1804C90E0, <c>InteractableGhostBossLeave.Teleport</c> 0x1804CB5B0).
    /// <para>Every peer runs this in the same order from the session seed, so generation matches.
    /// <b>UNVERIFIED, not run in-game.</b></para>
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
