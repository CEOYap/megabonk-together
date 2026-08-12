using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Steamworks;
using System;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Our own <c>CallResult</c>, injected into the IL2CPP domain and registered with the game's
    /// callback dispatcher, so that Steam's asynchronous results are <b>delivered</b> to us instead
    /// of being raced for.
    ///
    /// <para><b>Why this exists.</b> The game pumps <c>CallbackDispatcher.RunFrame</c> every frame,
    /// which drains the process's single manual-dispatch pipe and frees each message. Polling
    /// <c>GetAPICallResult</c> means competing with that pump for our own results, and losing is
    /// silent: the observed failure is <c>k_ESteamAPICallFailureInvalidHandle</c>, meaning the
    /// result was consumed before we read it. Polling every frame narrowed the window and did not
    /// close it — a lobby search still lost the race.</para>
    ///
    /// <para><b>So stop competing with the pump and become its customer.</b> Decompiled,
    /// <c>RunFrame</c> retrieves each completed call, looks the handle up in
    /// <c>m_registeredCallResults</c>, and invokes the registered <c>CallResult</c>. Registering
    /// there means the thing that was beating us hands us the result. It cannot lose the race,
    /// because it <i>is</i> the race.</para>
    ///
    /// <para><b>And it sidesteps the generics problem entirely.</b> The public API is
    /// <c>CallResult&lt;T&gt;</c>, which has no concrete IL2CPP instantiation for any lobby type —
    /// IL2CPP is ahead-of-time compiled and the game only ever used it for three leaderboard types.
    /// The <i>non-generic</i> base is abstract with three members and no type parameter anywhere,
    /// and its result arrives as a raw <c>IntPtr</c>, which is the shape this codebase already
    /// reads safely with <c>Marshal</c> at offsets from the dump.</para>
    ///
    /// <para>Precedent for injecting into a game class hierarchy: <c>CustomButton : MyButtonNormal</c>.
    /// This one derives from an <b>abstract</b> base, which is a stronger ask of
    /// <c>ClassInjector</c> — every abstract slot has to be filled or the type will not register.</para>
    /// </summary>
    internal class SteamCallResult : CallResult
    {
        /// <summary>Required by Il2CppInterop to wrap an existing native object.</summary>
        public SteamCallResult(IntPtr pointer) : base(pointer)
        {
        }

        public SteamCallResult() : base(ClassInjector.DerivedConstructorPointer<SteamCallResult>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }

        /// <summary>
        /// Runs when the dispatcher delivers this call's result: the raw result buffer, and whether
        /// Steam reported an IO failure.
        ///
        /// <para>Hidden from IL2CPP — a managed delegate has no bridge across the boundary, and
        /// nothing on the native side needs to see it. It is only ever set and invoked from managed
        /// code.</para>
        /// </summary>
        [HideFromIl2Cpp]
        internal Action<IntPtr, bool> Handler { get; set; }

        /// <summary>
        /// <b>Not used by the dispatch path.</b> <c>RunFrame</c> was decompiled to check: it keys
        /// on the call handle and invokes <see cref="OnRunCallResult"/> directly, and never asks
        /// what type we expect. This exists because the base declares it abstract, so it has to
        /// return something that is not null.
        /// </summary>
        public override Il2CppSystem.Type GetCallbackType() => Il2CppType.Of<LobbyCreated_t>();

        /// <summary>
        /// <para>Called from the game's own <c>Update</c>, so this is the main thread and the result
        /// can be applied immediately with no marshalling back.</para>
        ///
        /// <para><c>pvParam</c> belongs to the dispatcher and is freed the moment this returns, so
        /// everything needed must be read out now rather than kept.</para>
        /// </summary>
        public override void OnRunCallResult(IntPtr pvParam, bool bFailed, ulong hSteamAPICall)
        {
            try
            {
                Handler?.Invoke(pvParam, bFailed);
            }
            catch (Exception ex)
            {
                // Nothing above us is managed — this returns into the game's callback pump, and an
                // exception crossing that boundary would take out the game's own Steam handling
                // along with ours.
                Plugin.Log.LogError(
                    $"[steam-lobby] A call result handler threw, and was swallowed to protect the "
                    + $"game's callback pump: {ex}");
            }
        }

        public override void SetUnregistered()
        {
            Handler = null;
        }
    }
}
