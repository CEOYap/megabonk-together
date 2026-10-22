using System;

namespace OmniBonk.Services
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

    /// <summary>Starts and watches a netplay session (host, join by code, quickplay). No UI here.</summary>
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

        /// <summary>Raised on the main thread when <see cref="State"/> changes.</summary>
        event Action StateChanged;
    }
}
