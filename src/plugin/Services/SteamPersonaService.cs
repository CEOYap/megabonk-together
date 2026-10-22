using Il2CppInterop.Runtime;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OmniBonk.Services
{
    /// <summary>
    /// Steam display names cache. A stranger's name must be requested and arrives later via
    /// <c>PersonaStateChange_t</c>. See <c>docs/steamworks/03-observed-steam-usage.md</c>.
    /// </summary>
    internal class SteamPersonaService(ISteamService steamService, IPlayerManagerService playerManagerService)
    {
        /// <summary><c>PersonaStateChange_t.m_ulSteamID</c>, at offset 0.</summary>
        private const int PersonaSteamIdOffset = 0;

        private readonly Dictionary<ulong, string> names = [];

        private SteamCallback personaCallback;
        private bool registered;
        private bool appliedLocalName;

        /// <summary>The display name, or empty while still requested; callers should show a neutral fallback.</summary>
        public string GetName(ulong steamId)
        {
            if (steamId == 0UL)
            {
                return "";
            }

            if (names.TryGetValue(steamId, out var cached) && !string.IsNullOrEmpty(cached))
            {
                return cached;
            }

            if (!steamService.IsAvailable)
            {
                return "";
            }

            EnsureRegistered();

            try
            {
                var name = steamId == steamService.LocalSteamId
                    ? SteamFriends.GetPersonaName()
                    : SteamFriends.GetFriendPersonaName(new CSteamID(steamId));

                if (!string.IsNullOrEmpty(name))
                {
                    names[steamId] = name;
                    return name;
                }

                // RequestUserInformation returns false when already available: the name really is empty.
                if (steamId != steamService.LocalSteamId)
                {
                    SteamFriends.RequestUserInformation(new CSteamID(steamId), bRequireNameOnly: true);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam] Reading a persona name threw: {ex.GetType().Name}: {ex.Message}");
            }

            return "";
        }

        /// <summary>
        /// Once per launch, sets <c>ModConfig.PlayerName</c> (which feeds every display of it) to the
        /// Steam name. A later manual edit is kept.
        /// </summary>
        public void ApplyLocalPersonaName()
        {
            if (appliedLocalName || !steamService.IsAvailable)
            {
                return;
            }

            appliedLocalName = true;

            var persona = GetName(steamService.LocalSteamId);
            if (string.IsNullOrWhiteSpace(persona)
                || persona == Configuration.ModConfig.PlayerName.Value)
            {
                return;
            }

            var previous = Configuration.ModConfig.PlayerName.Value;
            Configuration.ModConfig.PlayerName.Value = persona;
            Configuration.ModConfig.Save();

            // Steam can finish starting after the lobby has already added us under the old name.
            var local = playerManagerService.GetLocalPlayer();
            if (local != null)
            {
                local.Name = persona;
                playerManagerService.UpdatePlayer(local);
            }

            Plugin.Log.LogInfo($"[steam] Using the Steam name '{persona}' (was '{previous}').");
        }

        private void EnsureRegistered()
        {
            if (registered)
            {
                return;
            }

            // Latched before the attempt, so a failure costs one log line rather than one per name.
            registered = true;

            try
            {
                personaCallback = new SteamCallback
                {
                    CallbackType = Il2CppType.Of<PersonaStateChange_t>(),
                    Handler = OnPersonaStateChange,
                };

                CallbackDispatcher.Register(personaCallback);
            }
            catch (Exception ex)
            {
                personaCallback = null;
                Plugin.Log.LogWarning(
                    $"[steam] Could not register for persona updates, so names of players who are "
                    + $"not friends may stay blank: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Invalidates the cached name; the next <see cref="GetName"/> re-reads it.</summary>
        private void OnPersonaStateChange(IntPtr payload)
        {
            var steamId = (ulong)Marshal.ReadInt64(payload, PersonaSteamIdOffset);
            if (steamId != 0UL)
            {
                names.Remove(steamId);
            }
        }
    }
}
