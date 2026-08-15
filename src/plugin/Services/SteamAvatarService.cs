using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Steam profile pictures, as sprites the lobby panel can drop into an <c>Image</c>.
    /// </summary>
    public interface ISteamAvatarService
    {
        /// <summary>
        /// The avatar for a Steam account, or null if it is not available yet.
        ///
        /// <para>Null is the ordinary answer the first time an account is asked about, not a
        /// failure: Steam fetches an avatar it does not have cached over the network. Ask again on
        /// a later frame — the caller here is a panel that redraws twice a second, so it does.</para>
        /// </summary>
        Sprite TryGetAvatar(ulong steamId);

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
    /// </summary>
    internal class SteamAvatarService(ISteamService steamService) : ISteamAvatarService
    {
        /// <summary>
        /// Steam's medium avatar, 64x64. Large is 184 and would be four times the pixels to convert
        /// for a row 28 units tall; small is 32 and visibly soft once the canvas scales it up.
        /// </summary>
        private const int MediumAvatarSize = 64;

        private const int BytesPerPixel = 4;

        private readonly Dictionary<ulong, Sprite> sprites = [];

        /// <summary>
        /// Accounts whose avatar could not be read, so a permanent failure is not retried at the
        /// panel's refresh rate. An account still <i>downloading</i> its avatar is not in here —
        /// that case has to stay retryable, which is the distinction this set exists to draw.
        /// </summary>
        private readonly HashSet<ulong> failed = [];

        public Sprite TryGetAvatar(ulong steamId)
        {
            if (steamId == 0UL || !steamService.IsAvailable || failed.Contains(steamId))
            {
                return null;
            }

            if (sprites.TryGetValue(steamId, out var cached) && cached != null)
            {
                return cached;
            }

            var sprite = Build(steamId);
            if (sprite != null)
            {
                sprites[steamId] = sprite;
            }

            return sprite;
        }

        public void Clear()
        {
            foreach (var sprite in sprites.Values)
            {
                if (sprite == null)
                {
                    continue;
                }

                // The texture is not owned by anything else, so it goes with the sprite. Leaving it
                // would leak 16 KB per member per session, which is small and unbounded.
                if (sprite.texture != null)
                {
                    UnityEngine.Object.Destroy(sprite.texture);
                }

                UnityEngine.Object.Destroy(sprite);
            }

            sprites.Clear();
            failed.Clear();
        }

        private Sprite Build(ulong steamId)
        {
            try
            {
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

                return ToSprite(buffer, (int)width, (int)height);
            }
            catch (Exception ex)
            {
                failed.Add(steamId);
                Plugin.Log.LogWarning($"[steam-avatar] Reading the avatar for {steamId} threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Turns Steam's raw RGBA into a sprite.
        ///
        /// <para><b>The rows are flipped, and that is not optional.</b> Steam hands the image back
        /// top-row-first; Unity's <c>SetPixels32</c> reads bottom-row-first. Copying straight
        /// through compiles, runs, and renders every avatar upside down.</para>
        /// </summary>
        private static Sprite ToSprite(Il2CppStructArray<byte> rgba, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                // Not tied to a scene: the panel is destroyed and rebuilt on every open, and the
                // cache outlives it.
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
            };

            var pixels = new Il2CppStructArray<Color32>(width * height);

            for (var y = 0; y < height; y++)
            {
                var sourceRow = y * width * BytesPerPixel;
                var targetRow = (height - 1 - y) * width;

                for (var x = 0; x < width; x++)
                {
                    var i = sourceRow + (x * BytesPerPixel);
                    pixels[targetRow + x] = new Color32(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, width, height),
                new Vector2(0.5f, 0.5f),
                MediumAvatarSize);

            sprite.hideFlags = HideFlags.HideAndDontSave;

            return sprite;
        }
    }
}
