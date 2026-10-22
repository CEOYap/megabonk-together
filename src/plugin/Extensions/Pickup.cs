using OmniBonk.Common.Models;

namespace OmniBonk.Extensions
{
    static internal class PickupExtensions
    {
        public static PickupModel ToModel(this Pickup pickup, uint pickupId)
        {
            return new PickupModel()
            {
                Id = pickupId,
                Position = pickup.transform.position.ToNumericsVector3(),
            };
        }
    }
}
