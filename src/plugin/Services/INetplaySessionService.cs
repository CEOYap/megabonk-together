using System;

namespace MegabonkTogether.Services
{
    public enum NetplayConnectState
    {
        Idle,

        /// <summary>Reaching the matchmaker.</summary>
        Connecting,

        /// <summary>Connected, waiting for a match. Quickplay only.</summary>
        WaitingForMatch,

        /// <summary>In a lobby. The lobby panel takes over from here.</summary>
        Ready,

        /// <summary><see cref="INetplaySessionService.StatusMessage"/> says why.</summary>
        Failed,
    }

    /// <summary>
    /// Starting and watching a netplay session: host, join by code, quickplay.
    ///
    /// <para><b>Extracted out of <c>NetworkMenuTab</c>, which is scheduled for deletion.</b> That
    /// class held this logic inside button handlers and two near-identical coroutines that also
    /// drove a loader, a Stop button and three screens' worth of visibility flags — so anything
    /// wanting to start a session had to open a menu and press its buttons. That is why accepting a
    /// Steam invite had to reach into the menu to join. See
    /// <c>docs/ui/05-drop-the-netplay-menu.md</c>.</para>
    ///
    /// <para>Nothing here touches UI. It exposes a state and a message; whoever is on screen shows
    /// them.</para>
    /// </summary>
    public interface INetplaySessionService
    {
        NetplayConnectState State { get; }

        /// <summary>What to show the player right now. Empty when there is nothing to say.</summary>
        string StatusMessage { get; }

        /// <summary>True while a connection attempt is in flight and cancellable.</summary>
        bool IsBusy { get; }

        /// <summary>Hosts a friendlies room. The room code arrives on <c>Plugin.Instance.Mode</c>.</summary>
        void Host();

        /// <summary>Joins a friendlies room by its code.</summary>
        void Join(string code);

        /// <summary>Enters the quickplay queue.</summary>
        void Quickplay();

        /// <summary>Aborts an attempt and resets networking. Safe when idle.</summary>
        void Cancel();

        /// <summary>
        /// Raised on the main thread whenever <see cref="State"/> changes, so a screen can react
        /// without polling. Also raised for <see cref="NetplayConnectState.Failed"/>.
        /// </summary>
        event Action StateChanged;
    }
}
