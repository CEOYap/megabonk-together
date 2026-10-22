// Developer test tooling: compiled into Debug builds only, never into a release (the release
// workflows build Release). See docs/netplay/05-local-testing.md.
#if DEBUG
using Assets.Scripts.UI.InGame.Rewards;
using UnityEngine;

namespace OmniBonk.Scripts.Bot
{
    /// <summary>
    /// Test bot: picks a random reward via the window's own controls, so reward patches see an ordinary pick.
    /// <para>Decompiled: <c>LevelupScreen</c>/<c>ChestWindowUi.ChooseOffer</c> are empty stubs, so those
    /// use button clicks; <c>EncounterUi.ChooseOffer(int)</c> (VA 0x18054D6F0) is real.</para>
    /// </summary>
    internal static class BotRewards
    {
        /// <summary>The game's input grace after a window opens, plus a margin.</summary>
        private const float OPEN_DELAY_SECONDS = 1f;

        private const float ACTION_INTERVAL_SECONDS = 0.5f;

        private static float nextActionTime;

        /// <summary>The open encounter window, or null.</summary>
        internal static BaseEncounterWindow ActiveWindow()
        {
            var window = UiManager.Instance?.encounterWindows?.activeEncounterWindow;
            return window != null && window.gameObject.activeInHierarchy ? window : null;
        }

        /// <summary>Call when a window has just opened, so the first action waits for the input grace.</summary>
        internal static void OnWindowOpened() => nextActionTime = Time.unscaledTime + OPEN_DELAY_SECONDS;

        /// <summary>Takes at most one action on <paramref name="window"/>; true when it acted.</summary>
        internal static bool TryPick(BaseEncounterWindow window)
        {
            if (window == null || Time.unscaledTime < nextActionTime)
            {
                return false;
            }

            nextActionTime = Time.unscaledTime + ACTION_INTERVAL_SECONDS;

            var levelup = window.GetComponent<LevelupScreen>();
            if (levelup != null)
            {
                return ClickRandomUpgrade(levelup.upgradePicker);
            }

            var chest = window.GetComponent<ChestWindowUi>();
            if (chest != null)
            {
                return Click(chest.b_open) || Click(chest.b_take);
            }

            var encounter = window.GetComponent<EncounterUi>();
            if (encounter != null)
            {
                var offers = encounter.offers;
                if (offers == null || offers.Length == 0)
                {
                    return false;
                }

                encounter.ChooseOffer(Random.Range(0, offers.Length));
                return true;
            }

            var microwave = window.GetComponent<MicrowaveUi>();
            if (microwave != null)
            {
                var items = microwave.itemPrefabs;
                if (items != null && items.Count > 0)
                {
                    var item = items[Random.Range(0, items.Count)];
                    if (item != null)
                    {
                        item.SelectUpgrade();
                        return true;
                    }
                }

                microwave.CloseScreen();
                return true;
            }

            return false;
        }

        /// <summary>Last resort when <see cref="TryPick"/> fails; an unclosed window stalls the shared barrier.</summary>
        internal static void Fallback(BaseEncounterWindow window)
        {
            if (window == null || Time.unscaledTime < nextActionTime)
            {
                return;
            }

            nextActionTime = Time.unscaledTime + 2f;

            var chest = window.GetComponent<ChestWindowUi>();
            if (chest != null)
            {
                Click(chest.b_take);
                return;
            }

            var levelup = window.GetComponent<LevelupScreen>();
            if (levelup != null)
            {
                if (!Click(levelup.b_skip))
                {
                    Click(levelup.b_leave);
                }

                return;
            }

            var microwave = window.GetComponent<MicrowaveUi>();
            if (microwave != null)
            {
                microwave.CloseScreen();
                return;
            }

            var encounter = window.GetComponent<EncounterUi>();
            if (encounter != null && encounter.offers != null && encounter.offers.Length > 0)
            {
                encounter.ChooseOffer(0);
            }
        }

        private static bool ClickRandomUpgrade(UpgradePicker picker)
        {
            var buttons = picker != null ? picker.buttons : null;
            if (buttons == null || buttons.Length == 0)
            {
                return false;
            }

            var active = new System.Collections.Generic.List<UpgradeButton>();
            foreach (var button in buttons)
            {
                if (button != null && button.gameObject.activeInHierarchy)
                {
                    active.Add(button);
                }
            }

            return active.Count > 0 && Click(active[Random.Range(0, active.Count)]);
        }

        /// <summary>Presses a game button the way a click does; false when it is not showing.</summary>
        private static bool Click(MyButton button)
        {
            if (button == null || button.button == null || !button.gameObject.activeInHierarchy)
            {
                return false;
            }

            button.button.onClick.Invoke();
            return true;
        }
    }
}
#endif
