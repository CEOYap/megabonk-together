namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Gets the derived game window behind a <c>Window</c> reference.
    ///
    /// <para><b>Why not <c>as</c>.</b> A C# cast only checks the managed wrapper Il2CppInterop
    /// handed back, and for a property typed <c>Window</c> that wrapper is often a plain
    /// <c>Window</c>, even when the native object is a <c>CharacterMenu</c>. The cast then
    /// returns null with nothing wrong in the game. Whether a derived wrapper happens to be cached
    /// depends on what other code fetched the same object before, so the failure comes and goes
    /// between sessions: on 2026-10-18 it threw 4,362 times on the host (<c>WindowManager.Update</c>)
    /// and once on the client (<c>WindowOpened</c>), in the same build that had passed character
    /// select earlier that day. <c>TryCast</c> would ask the native side, but <c>Window</c> compiles
    /// against <c>unity-libs</c> here and does not expose it (see <c>UiAssetService.ExtractPrefab</c>).</para>
    ///
    /// <para><b>Why <c>GetComponent</c>.</b> It resolves the type on the native side, so it returns
    /// the <c>CharacterMenu</c> component whatever wrapper the caller holds. A window is a
    /// component on its own GameObject, so this is the same object, not a search.</para>
    /// </summary>
    internal static class WindowCast
    {
        internal static CharacterMenu AsCharacterMenu(Window window)
        {
            if (window == null)
            {
                return null;
            }

            // Fast path: the wrapper is often already the derived one.
            return window as CharacterMenu ?? window.GetComponent<CharacterMenu>();
        }
    }
}
