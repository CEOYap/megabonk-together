// Developer test tooling: compiled into Debug builds only, never into a release (the release
// workflows build Release). See docs/netplay/05-local-testing.md.
#if DEBUG
using System;
using System.IO;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Hands the host's Friendlies room code to a test bot running in a second copy of the game on
    /// the same PC. Both copies resolve <see cref="Path"/> to the same file in the user's temp
    /// folder. Written on every Friendlies host, whatever <c>BotMode</c> says (design, call 4): it
    /// is a few bytes, and a second switch the owner forgot would make the bot silently never join.
    /// Never throws; an IO failure is logged once.
    /// </summary>
    internal static class BotRoomCodeFile
    {
        private static bool loggedFailure;

        internal static string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MegabonkTogether-bot-roomcode.txt");

        internal static void Write(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode))
            {
                return;
            }

            try
            {
                File.WriteAllText(Path, roomCode.Trim());
            }
            catch (Exception ex)
            {
                LogOnce($"could not write {Path}: {ex.Message}");
            }
        }

        /// <summary>The code, or null when the file is missing, empty or older than <paramref name="maxAge"/>.</summary>
        internal static string ReadFresh(TimeSpan maxAge)
        {
            try
            {
                if (!File.Exists(Path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(Path) > maxAge)
                {
                    return null;
                }

                var code = File.ReadAllText(Path).Trim();
                return code.Length == 0 ? null : code;
            }
            catch (Exception ex)
            {
                LogOnce($"could not read {Path}: {ex.Message}");
                return null;
            }
        }

        internal static void Delete()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch (Exception ex)
            {
                LogOnce($"could not delete {Path}: {ex.Message}");
            }
        }

        private static void LogOnce(string message)
        {
            if (loggedFailure)
            {
                return;
            }

            loggedFailure = true;
            Plugin.Log.LogWarning($"[bot] room-code file: {message}");
        }
    }
}
#endif
