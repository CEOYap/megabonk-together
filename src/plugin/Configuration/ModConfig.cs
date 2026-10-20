using BepInEx.Configuration;

namespace MegabonkTogether.Configuration
{
    public static class ModConfig
    {
        private static ConfigFile configFile;

        // DEV_URL is "ws://127.0.0.1:5000"

        public static ConfigEntry<string> PlayerName { get; private set; }
        public static ConfigEntry<bool> CheckForUpdates { get; private set; }
        public static ConfigEntry<string> ServerUrl { get; private set; }
        public static ConfigEntry<uint> RDVServerPort { get; private set; }
        public static ConfigEntry<bool> ShowChangelog { get; private set; }
        public static ConfigEntry<string> PreviousVersion { get; private set; }
        public static ConfigEntry<bool> AllowSavesDuringNetplay { get; private set; }
        public static ConfigEntry<bool> EnabledSharedExperience { get; private set; }
        public static ConfigEntry<bool> NoProjectileCap { get; private set; }
        public static ConfigEntry<float> EncounterInputGraceSeconds { get; private set; }
        public static ConfigEntry<bool> LogAllocationRate { get; private set; }
        public static ConfigEntry<bool> LogFrameTiming { get; private set; }
#if DEBUG
        // Developer test tooling, Debug builds only: a release has neither the bot nor the
        // loadout, so a player cannot turn either on by editing their config.
        public static ConfigEntry<bool> BotMode { get; private set; }
        public static ConfigEntry<bool> BotInvincible { get; private set; }
        public static ConfigEntry<float> BotFollowDistance { get; private set; }
        public static ConfigEntry<float> BotStuckSeconds { get; private set; }
        public static ConfigEntry<string> TestItems { get; private set; }
#endif
        public static ConfigEntry<bool> LogEncounterPause { get; private set; }
        public static ConfigEntry<bool> LogBandwidth { get; private set; }
        public static ConfigEntry<bool> LogDesync { get; private set; }
        public static ConfigEntry<bool> LogSteamStatus { get; private set; }
        public static ConfigEntry<bool> SteamLobbySelfTest { get; private set; }
        public static ConfigEntry<bool> SteamNetSelfTest { get; private set; }
        public static ConfigEntry<bool> UseSteamTransport { get; private set; }
        public static ConfigEntry<bool> LogUnityStackTraces { get; private set; }
        public static ConfigEntry<bool> LogUnityMessageStackTraces { get; private set; }

        public static void Initialize(ConfigFile config)
        {
            configFile = config;

            PlayerName = config.Bind(
                "Player",
                "PlayerName",
                "Player",
                "Your display name shown to other players. Please be respectful!"
            );
            CheckForUpdates = config.Bind(
                "Updates",
                "CheckForUpdates",
                true,
                "Check for updates on startup . Recommend leaving this enabled"
            );
            ServerUrl = config.Bind(
                "Network",
                "ServerUrl",
                "wss://megabonk-together-matchmaking.balatro-vs-matchmaking.eu",
                "The URL of the matchmaking server. Do not change this unless you know what you're doing (e.g. for self-hosting). Use ws://127.0.0.1:5000 on localhost for testing purpose"
            );
            RDVServerPort = config.Bind(
                "Network",
                "RDVServerPort",
                (uint)5678,
                "The port of the relay server. Do not change this unless you know what you're doing"
            );
            ShowChangelog = config.Bind(
                "Updates",
                "ShowChangelog",
                false,
                "Internal flag to show changelog after an update. Do not modify manually."
            );
            PreviousVersion = config.Bind(
                "Updates",
                "PreviousVersion",
                "",
                "Internal flag to store the previous version before an update. Do not modify manually."
            );
            LogAllocationRate = config.Bind(
                "Diagnostics",
                "LogAllocationRate",
                false,
                "Log the mod's own managed allocation rate every 10 seconds during a session. Off " +
                "by default. Turn it on when investigating stutter: the Unity Profiler cannot " +
                "attach to a retail IL2CPP build, so this is how GC pressure gets measured here."
            );
#if DEBUG
            BotMode = config.Bind(
                "Bot",
                "BotMode",
                false,
                "Developer tool, leave off for normal play. Turns this copy of the game into a test " +
                "bot that joins the host's lobby by itself, follows the host, cannot take damage, " +
                "picks rewards at random, uses nearby chests and shrines, and revives the host. " +
                "Meant for a second copy on the same PC (see docs/netplay/05-local-testing.md)."
            );
            BotInvincible = config.Bind(
                "Bot",
                "BotInvincible",
                true,
                "Developer tool. With BotMode on, whether the bot cannot take damage. Turn it off to " +
                "let the bot die, so a run can reach game over and the end screen, and so a bot's " +
                "death and revive can be tested. Needs a restart."
            );
            BotFollowDistance = config.Bind(
                "Bot",
                "BotFollowDistance",
                8f,
                "Developer tool. How far, in world units, the bot may drift from the host it follows."
            );
            TestItems = config.Bind(
                "Testing",
                "TestItems",
                "",
                "Developer tool, leave empty for normal play. Items this copy of the game is given " +
                "when a multiplayer run starts, comma-separated by internal item name, with an " +
                "optional count: \"IceCube, CursedDoll*2, Cactus\". Names are the game's EItem " +
                "names (Big Bonk is Bonker, Thunder Mitts is GloveLightning); an unknown name is " +
                "logged and skipped. Granted once per session, through the normal item path, so " +
                "every peer sees them. Each run also logs its end-screen damage rows (OB-45)."
            );
            BotStuckSeconds = config.Bind(
                "Bot",
                "BotStuckSeconds",
                6f,
                "Developer tool. Seconds without progress toward its target before the bot " +
                "teleports next to the host."
            );
#endif
            LogFrameTiming = config.Bind(
                "Diagnostics",
                "LogFrameTiming",
                false,
                "Log frame timing every 10 seconds during a session: average FPS, the 1% and 0.1% " +
                "lows, the worst frame, how many frames took over 33 ms and over 100 ms, gen-2 " +
                "garbage collections, and the enemy and player count at the time. Off by default. " +
                "Turn it on for performance testing; the lows and the >100 ms count are what show " +
                "stutter, which an average hides. Pairs with LogAllocationRate and LogBandwidth, " +
                "which report over the same 10 seconds."
            );
            LogEncounterPause = config.Bind(
                "Diagnostics",
                "LogEncounterPause",
                false,
                "Log whether the encounter-window pause actually holds while the window is open. " +
                "Off by default. Turn it on to investigate enemies taking damage during a reward " +
                "pop-up (OB-13): it prints one line whenever the window state, MyTime.paused or " +
                "Physics.simulationMode changes, and flags any moment the window is open without " +
                "the game being paused."
            );
            LogDesync = config.Bind(
                "Diagnostics",
                "LogDesync",
                false,
                "Compare the host's and each client's view of the run every 5 seconds and log what " +
                "disagrees: enemy and pickup counts, player level, the shared-experience barrier " +
                "round, and every interactable counter. Off by default. Turn it on on EVERY machine " +
                "for a test session, and only when everyone runs the same build - an older build " +
                "cannot read the message. Costs about 20 bytes per second. A mismatch is only " +
                "reported if it persists across two comparisons, so in-flight spawns are not noise."
            );
            LogBandwidth = config.Bind(
                "Diagnostics",
                "LogBandwidth",
                false,
                "Log outgoing bandwidth per message type every 10 seconds, plus the state of the " +
                "link to each peer. Off by default. Turn it on to find which stream is responsible " +
                "for a bandwidth problem, or to see how a session is holding up on a poor " +
                "connection - on the Steam transport this reports the measured delivery ratio and " +
                "the backlog of unacknowledged data, not just round-trip time."
            );
            LogSteamStatus = config.Bind(
                "Diagnostics",
                "LogSteamStatus",
                false,
                "Log the local SteamID and the state of Steam's relay network and authentication " +
                "every 10 seconds. Off by default. Turn it on when a Steam connection problem " +
                "needs to be told apart from a network one. Note that an instance launched " +
                "outside Steam has no Steam at all, which this will say."
            );
            SteamLobbySelfTest = config.Bind(
                "Diagnostics",
                "SteamLobbySelfTest",
                false,
                "Once per launch, create a private Steam lobby, write and read back lobby and " +
                "member data, then leave it, and log what happened. Off by default. This exists " +
                "to prove the Steamworks migration's lobby primitives on a real install; it " +
                "changes nothing about how you play and needs only one player."
            );
            SteamNetSelfTest = config.Bind(
                "Diagnostics",
                "SteamNetSelfTest",
                false,
                "Once per launch, open a Steam peer-to-peer listen socket, hold it for a couple of " +
                "seconds, then close it, and log what happened. Off by default. Steam has no " +
                "loopback so this cannot test an actual connection - it tests everything that has " +
                "to work before one is attempted, and needs only one player."
            );
            UseSteamTransport = config.Bind(
                "Network",
                "UseSteamTransport",
                true,
                "Carry netplay over Steam's peer-to-peer sockets instead of the matchmaking " +
                "server, using the Steam lobby as the session. On by default. Both players must " +
                "set this the same way - a Steam host and a matchmaker client cannot see each " +
                "other at all, and there is no in-game warning when they differ. Read at startup, " +
                "so changing it mid-session does nothing. If you played before this default " +
                "changed, your config file already has the old value and keeps it: set it to true " +
                "by hand, on both machines."
            );
            LogUnityStackTraces = config.Bind(
                "Diagnostics",
                "LogUnityStackTraces",
                false,
                "Re-enable Unity's script stack traces. Note that BepInEx's own LogOutput.log " +
                "records the exception message and discards the stack trace regardless of this " +
                "setting - read Unity's player log instead, at " +
                @"%USERPROFILE%\AppData\LocalLow\Ved\Megabonk\Player.log, which keeps " +
                "the full trace. Off by default because it changes logging for the whole game, " +
                "not just the mod."
            );
            LogUnityMessageStackTraces = config.Bind(
                "Diagnostics",
                "LogUnityMessageStackTraces",
                false,
                "Also record script stack traces for Unity's plain Log and Warning messages, such as " +
                "'Look rotation viewing vector is zero' (OB-25), in Unity's player log at " +
                @"%USERPROFILE%\AppData\LocalLow\Ved\Megabonk\Player.log. Needs LogUnityStackTraces " +
                "on too. Off by default: it adds a stack to every message the game logs, so the " +
                "player log grows quickly. Turn it on for one run, then off."
            );
            EncounterInputGraceSeconds = config.Bind(
                "Gameplay",
                "EncounterInputGraceSeconds",
                0.35f,
                "Ignore reward-window choices for this many seconds after the window opens. On a " +
                "controller the confirm button is also jump, and in Shared Experience a window can " +
                "open while you are mid-jump - without this, that jump press picks an item for you. " +
                "Set to 0 to disable."
            );
            AllowSavesDuringNetplay = config.Bind(
                "Gameplay",
                "AllowSavesDuringNetplay",
                false,
                "Allow game saves during netplay sessions."
            );
            EnabledSharedExperience = config.Bind(
                "Gameplay",
                "EnabledSharedExperience",
                false,
                "Enable Host experience (Same XP and pause enabled). Disable for no pause and separate XP."
            );
            NoProjectileCap = config.Bind(
                "Lobby",
                "NoProjectileCap",
                false,
                "Lets a weapon fire every projectile of a big volley at once. The game normally stops at 80 and drops the rest, " +
                "which only matters with very high Quantity. Only the host's setting counts: it applies to everyone in the session."
            );
        }

        public static void Save()
        {
            configFile?.Save();
        }
    }
}
