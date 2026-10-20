using Assets.Scripts._Data.MapsAndStages;
using System.Collections.Generic;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Endless Run's rooms. A regular room is one stage of a map; a boss room is a map's final boss
    /// stage. The values travel in <c>EndlessNextRoom.Room</c>, so they are wire format: append only,
    /// never renumber. The Graveyard rooms are reserved for Phase 3 and are not in the route yet.
    /// </summary>
    internal enum EndlessRoom
    {
        Forest1 = 0,
        Forest2 = 1,
        Forest3 = 2,
        Desert1 = 3,
        Desert2 = 4,
        Desert3 = 5,
        Crypt1 = 6,     // Phase 3
        Graveyard = 7,  // Phase 3
        Crypt2 = 8,     // Phase 3
        Ent = 9,
        Pharaoh = 10,
        GhostKing = 11, // Phase 3
    }

    internal static class EndlessRooms
    {
        /// <summary>Phase 2: Forest and Desert only.</summary>
        internal static readonly EndlessRoom[] Regular =
        [
            EndlessRoom.Forest1, EndlessRoom.Forest2, EndlessRoom.Forest3,
            EndlessRoom.Desert1, EndlessRoom.Desert2, EndlessRoom.Desert3,
        ];

        internal static readonly EndlessRoom[] Bosses = [EndlessRoom.Ent, EndlessRoom.Pharaoh];

        /// <summary>A boss room comes after this many regular rooms.</summary>
        internal const int RegularRoomsPerBoss = 3;

        internal static bool IsBoss(EndlessRoom room) => room is EndlessRoom.Ent or EndlessRoom.Pharaoh or EndlessRoom.GhostKing;

        internal static bool IsKnown(int value) => System.Enum.IsDefined(typeof(EndlessRoom), value);

        internal static EMap Map(EndlessRoom room) => room switch
        {
            EndlessRoom.Desert1 or EndlessRoom.Desert2 or EndlessRoom.Desert3 or EndlessRoom.Pharaoh => EMap.Desert,
            EndlessRoom.Crypt1 or EndlessRoom.Graveyard or EndlessRoom.Crypt2 or EndlessRoom.GhostKing => EMap.Graveyard,
            _ => EMap.Forest,
        };

        /// <summary>Stage index within the map. A boss room loads through LoadFinalStage instead.</summary>
        internal static int Stage(EndlessRoom room) => room switch
        {
            EndlessRoom.Forest2 or EndlessRoom.Desert2 => 1,
            EndlessRoom.Forest3 or EndlessRoom.Desert3 or EndlessRoom.Ent or EndlessRoom.Pharaoh => 2,
            _ => 0,
        };

        /// <summary>The room a stage is, or null when it is not one Endless knows (Phase 2: Forest and Desert).</summary>
        internal static EndlessRoom? FromStage(EMap map, int index, bool finalBoss)
        {
            if (map == EMap.Forest)
            {
                return finalBoss ? EndlessRoom.Ent : index switch { 0 => EndlessRoom.Forest1, 1 => EndlessRoom.Forest2, 2 => EndlessRoom.Forest3, _ => null };
            }

            if (map == EMap.Desert)
            {
                return finalBoss ? EndlessRoom.Pharaoh : index switch { 0 => EndlessRoom.Desert1, 1 => EndlessRoom.Desert2, 2 => EndlessRoom.Desert3, _ => null };
            }

            return null;
        }

        /// <summary>
        /// Fisher-Yates with the given generator, refilled from <paramref name="pool"/>. Never puts
        /// <paramref name="avoidFirst"/> at the front, so a room does not follow itself.
        /// </summary>
        internal static void Refill(List<EndlessRoom> queue, EndlessRoom[] pool, System.Random random, EndlessRoom? avoidFirst)
        {
            queue.AddRange(pool);
            for (var i = queue.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (queue[i], queue[j]) = (queue[j], queue[i]);
            }

            if (queue.Count > 1 && avoidFirst.HasValue && queue[0] == avoidFirst.Value)
            {
                (queue[0], queue[1]) = (queue[1], queue[0]);
            }
        }
    }
}
