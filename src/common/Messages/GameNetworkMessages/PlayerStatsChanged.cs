using System.Collections.Generic;
using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// OB-1. A player's global stat block, so other peers can draw that player's attacks with
    /// <i>their</i> numbers instead of the observer's.
    ///
    /// <para><b>The defect this exists for.</b> Every <c>WeaponUtility</c> stat accessor has the
    /// shape <c>weaponStats[stat] + PlayerStats.GetStat(stat)</c>, and <c>PlayerStats.GetStat</c> is
    /// static — it takes no player and reads whoever is asking. So a remote player's attack was
    /// built from their weapon and the observer's globals, and every visual derived from one was
    /// wrong by the difference between the two. Shield orbit count, aura size, projectile count and
    /// projectile size are all the same bug.</para>
    ///
    /// <para><b>Why a dictionary rather than named fields.</b> MemoryPack serializes positionally,
    /// so adding a field to a shipped message is as unsafe as renumbering a union tag. A keyed
    /// collection lets a later build carry another stat without a protocol change — which matters
    /// here because the affected set is "every stat a remote visual reads", and that list is a
    /// prediction rather than a measurement.</para>
    ///
    /// <para><b>Keyed by the game's <c>EStat</c> as an int</b>, deliberately not by an enum of our
    /// own: <c>src/common</c> cannot see game types, and a parallel enum would be one more thing to
    /// keep in step with the game across builds.</para>
    ///
    /// <para><b>Sent rarely.</b> Globals move on level-up and item pickup, so this is a handful of
    /// messages per run, not a stream.</para>
    /// </summary>
    [MemoryPackable]
    public partial class PlayerStatsChanged : IGameNetworkMessage
    {
        /// <summary>Whose stats these are.</summary>
        public uint OwnerId { get; set; }

        /// <summary>
        /// The owner's global stat values, keyed by <c>EStat</c> cast to int.
        ///
        /// <para>Only the stats that affect how somebody else's attack is drawn are carried. The
        /// receiver treats an absent key as "unknown" and leaves that visual alone rather than
        /// assuming zero — a zero would be a real value and would render the attack wrongly with
        /// confidence.</para>
        /// </summary>
        public Dictionary<int, float> Stats { get; set; } = new();
    }
}
