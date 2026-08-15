using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Steam profile pictures, as textures the lobby panel can drop into a <c>RawImage</c>.
    /// </summary>
    public interface ISteamAvatarService
    {
        /// <summary>
        /// The avatar for a Steam account, or null if it is not available yet.
        ///
        /// <para>Null is the ordinary answer the first time an account is asked about, not a
        /// failure: Steam fetches an avatar it does not have cached over the network. Ask again on
        /// a later frame — the caller here is a panel that redraws twice a second, so it does.</para>
        ///
        /// <para>A <c>Texture2D</c> for a <c>RawImage</c>, deliberately not a <c>Sprite</c> for an
        /// <c>Image</c> — see the note on <see cref="SteamAvatarService"/>.</para>
        /// </summary>
        Texture2D TryGetAvatar(ulong steamId);

        /// <summary>Drops every cached texture. Call when a session ends.</summary>
        void Clear();
    }

    /// <summary>
    /// Wraps Steam's avatar images in a cache, because the conversion is not free and the lobby
    /// panel asks for the same six faces twice a second.
    ///
    /// <para><b>Polled, not callback-driven.</b> Steam raises <c>AvatarImageLoaded_t</c> when an
    /// avatar finishes downloading, and taking that route would mean registering another callback
    /// on the shared pipe. It buys nothing here: the only consumer already redraws on a timer, so
    /// asking again and getting an answer one refresh later is the same outcome with none of the
    /// registration. <c>docs/steamworks/00-migration-plan.md</c>'s standing preference is to add no
    /// dispatcher that is not needed.</para>
    ///
    /// <para><b>All main-thread.</b> The image is at most 184x184, so the copy and the conversion
    /// are trivial next to the frame they run in, and doing it inline avoids handing Unity texture
    /// calls to a worker thread — which is a hard crash with no managed stack, not a race.</para>
    ///
    /// <para><b>Nothing here passes an array to Unity, and that is the whole shape of this class.</b>
    /// The plugin compiles <c>UnityEngine.CoreModule</c> from <c>unity-libs</c> — plain managed
    /// Unity — while the runtime loads the Il2CppInterop proxy, so any Unity method taking an array
    /// is declared <c>T[]</c> at compile time and <c>Il2CppStructArray&lt;T&gt;</c> at run time. It
    /// links, and then throws <c>MissingMethodException</c> the first time it is called. The first
    /// version of this used <c>SetPixels32</c> and did exactly that. The pixels now go in through
    /// <c>LoadRawTextureData(IntPtr, int)</c>, whose parameters are primitives, and the result is a
    /// <c>Texture2D</c> for a <c>RawImage</c> rather than a <c>Sprite</c>, because
    /// <c>Sprite.Create</c> is another boundary call that did not need to be in the path.</para>
    /// </summary>
    internal class SteamAvatarService(ISteamService steamService) : ISteamAvatarService
    {
        private const int BytesPerPixel = 4;

        private readonly Dictionary<ulong, Texture2D> textures = [];

        /// <summary>
        /// Accounts whose avatar could not be read, so a permanent failure is not retried at the
        /// panel's refresh rate. An account still <i>downloading</i> its avatar is not in here —
        /// that case has to stay retryable, which is the distinction this set exists to draw.
        /// </summary>
        private readonly HashSet<ulong> failed = [];

        public Texture2D TryGetAvatar(ulong steamId)
        {
            if (steamId == 0UL || !steamService.IsAvailable || failed.Contains(steamId))
            {
                return null;
            }

            if (textures.TryGetValue(steamId, out var cached) && cached != null)
            {
                return cached;
            }

            var texture = Build(steamId);
            if (texture != null)
            {
                textures[steamId] = texture;
            }

            return texture;
        }

        public void Clear()
        {
            foreach (var texture in textures.Values)
            {
                // Nothing else owns these — they are built here and handed to a RawImage that does
                // not take ownership. Left alone they would leak 16 KB per member per session.
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }

            textures.Clear();
            failed.Clear();
        }

        private Texture2D Build(ulong steamId)
        {
            try
            {
                // Medium is 64x64. Large is 184 and four times the pixels to flip for a row 28
                // units tall; small is 32 and visibly soft once the canvas scales it up.
                var handle = SteamFriends.GetMediumFriendAvatar(new CSteamID(steamId));

                // 0 means Steam does not have it yet and is now fetching it; a later call answers.
                // Negative means it will never answer for this account.
                if (handle == 0)
                {
                    // Nudges Steam into fetching the persona and its avatar for someone who is not
                    // a friend — which every stranger in a lobby is.
                    SteamFriends.RequestUserInformation(new CSteamID(steamId), bRequireNameOnly: false);
                    return null;
                }

                if (handle < 0)
                {
                    failed.Add(steamId);
                    return null;
                }

                if (!SteamUtils.GetImageSize(handle, out var width, out var height)
                    || width == 0
                    || height == 0)
                {
                    failed.Add(steamId);
                    Plugin.Log.LogWarning($"[steam-avatar] GetImageSize failed for {steamId}.");
                    return null;
                }

                var byteCount = (int)(width * height * BytesPerPixel);
                var buffer = new Il2CppStructArray<byte>(byteCount);

                if (!SteamUtils.GetImageRGBA(handle, buffer, byteCount))
                {
                    failed.Add(steamId);
                    Plugin.Log.LogWarning($"[steam-avatar] GetImageRGBA failed for {steamId}.");
                    return null;
                }

                return ToTexture(buffer, (int)width, (int)height);
            }
            catch (Exception ex)
            {
                failed.Add(steamId);
                Plugin.Log.LogWarning($"[steam-avatar] Reading the avatar for {steamId} threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Turns Steam's raw RGBA into a texture.
        ///
        /// <para><b>The rows are flipped, and that is not optional.</b> Steam hands the image back
        /// top-row-first and Unity's raw texture data is bottom-row-first, so copying straight
        /// through compiles, runs, and renders every avatar upside down.</para>
        ///
        /// <para><b>Uploaded through a pinned pointer</b> rather than any of the array-taking
        /// overloads. RGBA32 is byte-for-byte what Steam already handed over, so the only work is
        /// the flip, and <c>LoadRawTextureData(IntPtr, int)</c> takes nothing that has to be
        /// marshalled — which is the point. See the note on this class.</para>
        /// </summary>
        private static Texture2D ToTexture(Il2CppStructArray<byte> rgba, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                // Not tied to a scene: the panel is destroyed and rebuilt on every open, and the
                // cache outlives it.
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
            };

            var stride = width * BytesPerPixel;
            var flipped = new byte[stride * height];

            for (var y = 0; y < height; y++)
            {
                var source = y * stride;
                var target = (height - 1 - y) * stride;

                for (var i = 0; i < stride; i++)
                {
                    flipped[target + i] = rgba[source + i];
                }
            }

            var pin = GCHandle.Alloc(flipped, GCHandleType.Pinned);
            try
            {
                texture.LoadRawTextureData(pin.AddrOfPinnedObject(), flipped.Length);
                texture.Apply();
            }
            finally
            {
                pin.Free();
            }

            return texture;
        }
    }
}
