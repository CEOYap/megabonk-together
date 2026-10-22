using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace OmniBonk.Services
{
    public interface ISteamAvatarService
    {
        /// <summary>Null until Steam has downloaded the avatar; ask again on a later frame.</summary>
        Texture2D TryGetAvatar(ulong steamId);

        /// <summary>Call when a session ends.</summary>
        void Clear();
    }

    /// <summary>
    /// Cached Steam avatars. Polled, not callback-driven. Main thread only (Unity texture calls
    /// off-thread hard-crash).
    /// <para>IL2CPP: never pass an array to Unity. Array parameters are <c>T[]</c> at compile time but
    /// <c>Il2CppStructArray</c> at run time and throw <c>MissingMethodException</c>; hence
    /// <c>LoadRawTextureData(IntPtr, int)</c> and a <c>Texture2D</c> rather than <c>Sprite.Create</c>.</para>
    /// </summary>
    internal class SteamAvatarService(ISteamService steamService) : ISteamAvatarService
    {
        private const int BytesPerPixel = 4;

        private readonly Dictionary<ulong, Texture2D> textures = [];

        /// <summary>Permanent read failures only; still-downloading avatars must stay retryable.</summary>
        private readonly HashSet<ulong> failed = [];

        private readonly Dictionary<ulong, float> lastUserInformationRequest = [];

        private const float UserInformationRetrySeconds = 1f;

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
                // Owned here; RawImage does not take ownership.
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }

            textures.Clear();
            failed.Clear();
            lastUserInformationRequest.Clear();
        }

        /// <summary>Throttled per account; the panel refreshes constantly.</summary>
        private void RequestUserInformationThrottled(ulong steamId)
        {
            var now = Time.unscaledTime;

            if (lastUserInformationRequest.TryGetValue(steamId, out var last)
                && now - last < UserInformationRetrySeconds)
            {
                return;
            }

            lastUserInformationRequest[steamId] = now;
            SteamFriends.RequestUserInformation(new CSteamID(steamId), bRequireNameOnly: false);
        }

        private Texture2D Build(ulong steamId)
        {
            try
            {
                var handle = SteamFriends.GetMediumFriendAvatar(new CSteamID(steamId));

                // 0 = no avatar, -1 = not loaded yet. Neither is a failure: never latch them into `failed`.
                if (handle <= 0)
                {
                    RequestUserInformationThrottled(steamId);
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
        /// Rows are flipped: Steam is top-row-first, Unity raw data bottom-row-first. Uploaded via a
        /// pinned pointer to avoid array overloads.
        /// </summary>
        private static Texture2D ToTexture(Il2CppStructArray<byte> rgba, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                // The cache outlives the panel.
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
