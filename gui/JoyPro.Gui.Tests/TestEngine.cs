namespace JoyPro.Gui.Tests;

// The engine build of the same configuration as the tests (a Release test run must not start a Debug engine left
// over from an older build).
internal static class TestEngine
{
#if DEBUG
    public const string Configuration = "Debug";
#else
    public const string Configuration = "Release";
#endif
}
