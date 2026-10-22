using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Steamworks;
using System;

namespace OmniBonk.Services
{
    /// <summary>
    /// Our own <c>CallResult</c>, registered with the game's dispatcher so Steam results are delivered
    /// to us. Polling <c>GetAPICallResult</c> silently loses the race to the game's <c>RunFrame</c> (decompiled).
    /// <para>Non-generic base: <c>CallResult&lt;T&gt;</c> has no IL2CPP instantiation for lobby types.
    /// Every abstract member must be overridden or <c>ClassInjector</c> will not register the type.</para>
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

        /// <summary>(result buffer, IO failure). Hidden from IL2CPP: managed delegates cannot cross.</summary>
        [HideFromIl2Cpp]
        internal Action<IntPtr, bool> Handler { get; set; }

        /// <summary>Unused by <c>RunFrame</c> (decompiled; keys on the handle). Abstract, so must return non-null.</summary>
        public override Il2CppSystem.Type GetCallbackType() => Il2CppType.Of<LobbyCreated_t>();

        /// <summary>Main thread. <c>pvParam</c> is freed on return; read everything now.</summary>
        public override void OnRunCallResult(IntPtr pvParam, bool bFailed, ulong hSteamAPICall)
        {
            try
            {
                Handler?.Invoke(pvParam, bFailed);
            }
            catch (Exception ex)
            {
                // Must not cross into the game's callback pump.
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
