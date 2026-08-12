using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Steamworks;
using System;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Our own <c>Callback</c>, injected into the IL2CPP domain and registered with the game's
    /// dispatcher — the same trick <see cref="SteamCallResult"/> uses, applied to plain callbacks
    /// rather than call results.
    ///
    /// <para><b>Why not <c>Callback&lt;T&gt;</c>.</b> IL2CPP is ahead-of-time compiled, so a generic
    /// exists only for the type arguments the game itself used: <c>GameOverlayActivated_t</c>,
    /// <c>PersonaStateChange_t</c> and <c>UserStatsReceived_t</c>. Nothing this mod wants has a
    /// concrete instantiation. The non-generic base has no type parameter and hands its payload over
    /// as a raw <c>IntPtr</c>, which is the shape we already read with <c>Marshal</c> at offsets
    /// from the dump.</para>
    ///
    /// <para><b><see cref="GetCallbackType"/> is load-bearing here, unlike on
    /// <see cref="SteamCallResult"/>.</b> A call result is looked up by its call handle, but a
    /// callback is keyed by callback id, and <c>CallbackDispatcher.Register</c> derives that id by
    /// passing whatever this returns to <c>CallbackIdentities.GetCallbackIdentity</c>, which reads
    /// the <c>[CallbackIdentity(N)]</c> attribute off the struct. Return the wrong type and the
    /// callback is silently filed under the wrong id and never fires.</para>
    /// </summary>
    internal class SteamCallback : Callback
    {
        /// <summary>Required by Il2CppInterop to wrap an existing native object.</summary>
        public SteamCallback(IntPtr pointer) : base(pointer)
        {
        }

        public SteamCallback() : base(ClassInjector.DerivedConstructorPointer<SteamCallback>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }

        /// <summary>
        /// The struct this callback is for, e.g. <c>Il2CppType.Of&lt;GameLobbyJoinRequested_t&gt;()</c>.
        /// Set before registering; the dispatcher reads it to work out the callback id.
        /// </summary>
        [HideFromIl2Cpp]
        internal Il2CppSystem.Type CallbackType { get; set; }

        /// <summary>
        /// Runs on each delivery, with the raw payload. Hidden from IL2CPP: a managed delegate has
        /// no bridge across the boundary and nothing native needs to see it.
        /// </summary>
        [HideFromIl2Cpp]
        internal Action<IntPtr> Handler { get; set; }

        /// <summary>Always a client here. The mod never runs a Steam game server.</summary>
        public override bool IsGameServer => false;

        public override Il2CppSystem.Type GetCallbackType() => CallbackType;

        /// <summary>
        /// <para>Called from the game's own <c>Update</c> via its callback pump, so this is the main
        /// thread.</para>
        ///
        /// <para><c>pvParam</c> belongs to the dispatcher and is freed as soon as this returns —
        /// read what is needed now, do not keep the pointer.</para>
        /// </summary>
        public override void OnRunCallback(IntPtr pvParam)
        {
            try
            {
                Handler?.Invoke(pvParam);
            }
            catch (Exception ex)
            {
                // This returns into the game's callback pump. An exception crossing that boundary
                // would take the game's own Steam handling down with ours.
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
