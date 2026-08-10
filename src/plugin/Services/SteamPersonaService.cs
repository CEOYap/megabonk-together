using Il2CppInterop.Runtime;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Steam display names, cached, because asking for one is not a getter.
    ///
    /// <para><c>GetFriendPersonaName</c> returns nothing useful for somebody the client has no
    /// information about yet — which includes most people you are in a lobby with and are not
    /// friends with. The name has to be <i>requested</i>, and it arrives later as a
    /// <c>PersonaStateChange_t</c>. So this is a cache with a callback filling it, not a lookup.</para>
    ///
    /// <para>Same shape a shipping implementation for this game uses; see
    /// <c>docs/steamworks/03-observed-steam-usage.md</c>.</para>
    /// </summary>
    internal class SteamPersonaService(ISteamService steamService)
    {
        /// <summary><c>PersonaStateChange_t.m_ulSteamID</c>, at offset 0.</summary>
        private const int PersonaSteamIdOffset = 0;

        private readonly Dictionary<ulong, string> names = [];

        private SteamCallback personaCallback;
        private bool registered;

        /// <summary>
        /// The display name for a SteamID, or empty if it is not known yet.
        ///
        /// <para>Empty is normal rather than an error the first time a stranger is seen: the request
        /// has been sent and the name will be there on a later call. Callers should fall back to
        /// something neutral rather than showing a blank row.</para>
        /// </summary>
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

                // Not known yet. Ask, and the callback fills the cache. RequestUserInformation
                // returns false when the information is already available, which here means the
                // name really is empty rather than pending.
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

        /// <summary>
        /// Someone's details arrived or changed. Dropped from the cache rather than re-read here:
        /// the next <see cref="GetName"/> fetches it, and that keeps the Steam call on the path
        /// that actually wants the answer.
        /// </summary>
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
