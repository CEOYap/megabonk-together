using HarmonyLib;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(CharacterMenu))]
    internal static class CharacterMenuPatches
    {
        /// <summary>Caches character icons for the player cards.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(CharacterMenu.TryInit))]
        public static void TryInit_Postfix(CharacterMenu __instance)
        {
            Plugin.Instance.ClearCharacterIcons();
            Plugin.Instance.AddCharacterIcons(__instance.characterButtons);
        }
    }
}
