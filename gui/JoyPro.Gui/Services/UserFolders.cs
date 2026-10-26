using System;
using System.IO;

namespace JoyPro.Gui.Services;

// The user's folders the app prefills paths from (Saved Games, Documents, Program Files). With the test sandbox
// (JOYPRO_TEST_SANDBOX=<root>, docs/testing.md "Test sandbox") each one is a folder under <root>\UserFolders instead,
// so a test's default DCS, Star Citizen, profile or report path is never a real game or user folder; the engine
// refuses every write outside the sandbox in the same runs.
internal static class UserFolders
{
    public const string SandboxVariable = "JOYPRO_TEST_SANDBOX";

    public static string? SandboxRoot
    {
        get
        {
            string? root = Environment.GetEnvironmentVariable(SandboxVariable);
            return string.IsNullOrWhiteSpace(root) ? null : root.Trim().Trim('"');
        }
    }

    public static bool IsSandboxed => SandboxRoot != null;

    public static string Get(Environment.SpecialFolder folder)
    {
        string? root = SandboxRoot;
        return root == null ? Environment.GetFolderPath(folder) : Path.Combine(root, "UserFolders", folder.ToString());
    }

    // True for a path inside the sandbox (always true without one): a game path found on the real PC is not taken over.
    public static bool InSandbox(string? path)
    {
        string? root = SandboxRoot;
        if (root == null)
            return true;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            string top = Path.GetFullPath(root).TrimEnd('\\', '/');
            return full.Equals(top, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
