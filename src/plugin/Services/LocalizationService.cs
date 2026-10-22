using System.Collections.Generic;
using System.Linq;

namespace OmniBonk.Services
{
    public interface ILocalizationService
    {
        public string GetCustomLocalizedString(string category, string key);
        public void EnqueueNextLocalizedDescription(IEnumerable<string> descriptionArgs);

        public string GetNextCustomLocalizedDescription(string category, string key);
    }
    public class LocalizationService : ILocalizationService
    {
        public readonly Dictionary<(string, string), string> LocalizationOverrides = new()
        {
            { ("OmniBonk", "MatchSuccess"), "Match" },
            { ("OmniBonk", "MatchSuccessDesc"), "Joined as {0} a lobby of {1} players successfully!" },
            { ("OmniBonk", "PlayerDisconnected"), "Disconnected" },
            { ("OmniBonk", "PlayerDisconnected_Description"), "Player {0} disconnected" },
            { ("OmniBonk", "AllPlayerDisconnected"), "Disconnected" },
            { ("OmniBonk", "AllPlayerDisconnected_Description"), "All players left!" },
            { ("OmniBonk", "HostDisconnected"), "Disconnected" },
            { ("OmniBonk", "HostDisconnected_Description"), "Host left!" },
            { ("OmniBonk", "ClientDisconnected"), "Disconnected" },
            { ("OmniBonk", "ClientDisconnected_Description"), "Client disconnected" },
            { ("OmniBonk", "FriendliesHostSuccess"), "Friendlies" },
            { ("OmniBonk", "FriendliesHostSuccessDesc"), "Hosting a friendly lobby" },
            { ("OmniBonk", "FriendliesClientSuccess"), "Friendlies" },
            { ("OmniBonk", "FriendliesClientSuccessDesc"), "Joined {0} lobby" },
            { ("OmniBonk", "FriendliesClientJoinSuccess"), "Friendlies" },
            { ("OmniBonk", "FriendliesClientJoinSuccessDesc"), "{0} joined " },
            { ("OmniBonk", "VersionMismatch"), "Different version" },
            { ("OmniBonk", "VersionMismatch_Description"), "{0} has a different mod version. Everyone needs the same version to start." }
        };

        private readonly Queue<IEnumerable<string>> NextLocalizedDescription = new();

        public string GetCustomLocalizedString(string category, string key)
        {
            if (LocalizationOverrides.TryGetValue((category, key), out var localizedValue))
            {
                return localizedValue;
            }
            return string.Empty;
        }

        public void EnqueueNextLocalizedDescription(IEnumerable<string> descriptionArgs)
        {
            NextLocalizedDescription.Enqueue(descriptionArgs);
        }

        public string GetNextCustomLocalizedDescription(string category, string key)
        {
            var customString = GetCustomLocalizedString(category, key);
            if (string.IsNullOrEmpty(customString))
            {
                return "";
            }

            if (NextLocalizedDescription.Count > 0)
            {
                var args = NextLocalizedDescription.Dequeue();
                try
                {
                    return string.Format(customString, args.ToArray());
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"Error formatting localized string: {ex.Message}");
                    return customString;
                }
            }

            return customString;
        }
    }
}
