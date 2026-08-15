using MegabonkTogether.Scripts.Modal;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Scripts.Button
{
    public class PlayTogetherButton : MyButtonNormal
    {
        private MainMenu mainMenu;

        public override void OnClick()
        {
            OpenLobby();
        }

        /// <summary>
        /// Resolved per use rather than cached, because this is a button press — not a per-frame
        /// path — and a static field on a type <c>ClassInjector</c> registers would run its
        /// initialiser before the DI host exists.
        /// </summary>
        private static INetplaySessionService SessionService =>
            Plugin.Services.GetRequiredService<INetplaySessionService>();

        /// <summary>
        /// TOGETHER! goes straight to the lobby, hosting.
        ///
        /// <para><b>It always hosts, and that is the design rather than a simplification.</b> One
        /// button cannot both create a lobby and join one, and joining already has two better
        /// routes into it — an invite, and Join From Clipboard on the panel. Step 2 of
        /// <c>docs/ui/05-drop-the-netplay-menu.md</c>.</para>
        ///
        /// <para>This replaces <c>OpenNetworkTab</c>, which built <c>NetworkMenuTab</c> — a name
        /// box, a Random/Friendlies choice and a second screen behind it, all of which existed
        /// because the mod had nowhere else to put a control. The panel is that somewhere.</para>
        /// </summary>
        internal LobbyPanel OpenLobby() => StartAndShow(SessionService.Host);

        /// <summary>
        /// The same thing for an accepted Steam invite, which joins rather than hosts.
        ///
        /// <para>Exposed so the invite can act without a player pressing anything, and kept here
        /// rather than at the caller because <see cref="mainMenu"/> lives on this component and the
        /// panel cannot build itself without it.</para>
        /// </summary>
        internal LobbyPanel JoinLobby(string code) => StartAndShow(() => SessionService.Join(code));

        /// <summary>
        /// Opens the panel and starts a session behind it — but <b>only if the panel was not
        /// already up</b>.
        ///
        /// <para>That guard is the whole reason this is one method instead of two copies. The panel
        /// is now the netplay UI, so "already open" means a session is already running or
        /// connecting, and starting a second one is not a no-op: the session service refuses a
        /// start only while it is <i>busy</i>, so a second press once a lobby is up passes the
        /// check and tears down the working session to begin another. The panel opening twice would
        /// have been harmless; the session restarting is not.</para>
        ///
        /// <para>The panel is created before the session starts, so a failure has something to be
        /// reported on. It shows its not-in-a-lobby state and follows the service from there.</para>
        /// </summary>
        private LobbyPanel StartAndShow(System.Action startSession)
        {
            var existing = LobbyPanel.Current;
            if (existing != null)
            {
                return existing;
            }

            var panel = LobbyPanel.Open(mainMenu);

            startSession();

            return panel;
        }

        public void SetMainMenu(MainMenu menu)
        {
            mainMenu = menu;
        }
    }
}
