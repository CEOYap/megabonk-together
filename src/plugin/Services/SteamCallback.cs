using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Steamworks;
using System;

namespace OmniBonk.Services
{
    /// <summary>
    /// A non-generic <c>Callback</c> injected into IL2CPP and registered with the game's dispatcher.
    /// <c>Callback&lt;T&gt;</c> exists only for the game's own type arguments (AOT), so the payload
    /// arrives as a raw <c>IntPtr</c>.
    /// <para><see cref="GetCallbackType"/> is load-bearing: the dispatcher derives the callback id
    /// from it, and a wrong type means the callback silently never fires.</para>
    /// </summary>
    internal class SteamCallback : Callback
    {
        /// <summary>Required by Il2CppInterop.</summary>
        public SteamCallback(IntPtr pointer) : base(pointer)
        {
        }

        public SteamCallback() : base(ClassInjector.DerivedConstructorPointer<SteamCallback>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }

        /// <summary>E.g. <c>Il2CppType.Of&lt;GameLobbyJoinRequested_t&gt;()</c>. Set before registering.</summary>
        [HideFromIl2Cpp]
        internal Il2CppSystem.Type CallbackType { get; set; }

        /// <summary>Hidden from IL2CPP: managed delegates have no bridge.</summary>
        [HideFromIl2Cpp]
        internal Action<IntPtr> Handler { get; set; }

        public override bool IsGameServer => false;

        public override Il2CppSystem.Type GetCallbackType() => CallbackType;

        /// <summary>
        /// Main thread (game's callback pump). <c>pvParam</c> is freed on return; never keep it.
        /// </summary>
        public override void OnRunCallback(IntPtr pvParam)
        {
            try
            {
                Handler?.Invoke(pvParam);
            }
            catch (Exception ex)
            {
                // Must not propagate into the game's callback pump.
                Plugin.Log.LogError(
                    $"[steam] A callback handler threw, and was swallowed to protect the game's "
                    + $"callback pump: {ex}");
            }
        }

        public override void SetUnregistered()
        {
            Handler = null;
        }
    }
}
