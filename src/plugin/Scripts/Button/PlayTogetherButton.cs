using UnityEngine;

namespace MegabonkTogether.Scripts.Button
{
    public class PlayTogetherButton : MyButtonNormal
    {
        private MainMenu mainMenu;

        public override void OnClick()
        {
            OnPlayTogetherClick();
        }


        private void OnPlayTogetherClick()
        {
            OpenNetworkTab();
        }

        /// <summary>
        /// Opens the netplay menu, exactly as pressing this button does.
        ///
        /// <para>Exposed so an accepted Steam invite can open it without a player pressing
        /// anything. Kept here rather than duplicated at the caller because <c>SetMainMenu</c> has
        /// to happen for the tab to be able to build itself, and forgetting it produces a screen
        /// with no buttons.</para>
        /// </summary>
        internal NetworkMenuTab OpenNetworkTab()
        {
            //ButtonManager.selectedButton2 = this;

            var networkMenuObj = new GameObject("NetworkMenuTab");
            Plugin.Instance.NetworkTab = networkMenuObj.AddComponent<NetworkMenuTab>();
            Plugin.Instance.NetworkTab.SetMainMenu(mainMenu);
            return Plugin.Instance.NetworkTab;
        }

        public void SetMainMenu(MainMenu menu)
        {
            mainMenu = menu;
        }
    }
}
