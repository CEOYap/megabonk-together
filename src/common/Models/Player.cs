using MegabonkTogether.Common.Messages;
using MemoryPack;

namespace MegabonkTogether.Common.Models
{
    [MemoryPackable]
    public partial class Player
    {
        public uint ConnectionId;
        public bool IsHost = false;
        public uint Character = 0;
        public string Skin = "";
        public bool IsReady = false;
        public string Name = "Player";
        public QuantizedVector3 Position = new();
        public AnimatorState AnimatorState { get; set; } = new();
        public MovementState MovementState { get; set; } = new();

        public InventoryInfo Inventory { get; set; } = new();

        public uint Hp = 100;
        public uint MaxHp = 100;
        //public uint Xp = 0;
        public uint Shield = 0;
        public uint MaxShield = 0;

        /// <summary>
        /// The player's hat, as an <c>EHat</c>.
        ///
        /// <para><b>Replicated rather than announced.</b> Hats used to travel only as a
        /// <c>HatChanged</c> event, so a peer that already had one when you joined - or whose avatar
        /// was rebuilt on a level transition - never got told about it and appeared bare-headed
        /// forever. An event says a hat *changed*; only the record says what it *is*, which is what
        /// an avatar built at an arbitrary moment needs to read.</para>
        /// </summary>
        public uint Hat = 0;

    }
}
