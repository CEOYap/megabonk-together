using OmniBonk.Scripts.Modal;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Scripts.Button
{
    public class PlayTogetherButton : MyButtonNormal
    {
        private MainMenu mainMenu;

        public override void OnClick()
        {
            OpenLobby();
        }

        /// <summary>Not a static field: on a ClassInjector type it would initialise before the DI host exists.</summary>
        private static INetplaySessionService SessionService =>
            Plugin.Services.GetRequiredService<INetplaySessionService>();

        /// <summary>Always hosts; joining is via invite or Join From Clipboard.</summary>
        internal LobbyPanel OpenLobby() => StartAndShow(SessionService.Host);

        /// <summary>For an accepted Steam invite (joins). Here because the panel needs <see cref="mainMenu"/>.</summary>
        internal LobbyPanel JoinLobby(string code) => StartAndShow(() => SessionService.Join(code));

        /// <summary>
        /// Starts a session only if the panel was not already up; a second start would tear down a
        /// working lobby. Panel first, so failures have somewhere to show.
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
