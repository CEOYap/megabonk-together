namespace OmniBonk.Helpers
{
    /// <summary>
    /// Gets the derived window behind a <c>Window</c> reference. Not <c>as</c>: it tests the cached
    /// Il2CppInterop wrapper, which is often a plain <c>Window</c> (intermittent nulls). <c>TryCast</c>
    /// is not exposed here, so <c>GetComponent</c> resolves the type natively.
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
