using System;
using System.Collections.Generic;
using System.IO;
using JoyPro.Engine;
using Xunit;

namespace JoyPro.Gui.Tests;

// The engine's file copies and replaces for the exports (engine\SafeFileIo.cs, linked): a file carrying the EFS
// "encrypted" mark failed an MSFS 2024 export with "The specified file could not be encrypted." (ERROR_ENCRYPTION_FAILED,
// HRESULT 0x80071770) when File.Copy backed up the preset. The native calls are made to fail like that here.
[Collection("SafeFileIo")]
public sealed class SafeFileIoTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "safefileio-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> faults = [];

    public SafeFileIoTests()
    {
        Directory.CreateDirectory(folder);
        SafeFileIo.TestFault = operation =>
        {
            if (faults.Contains(operation))
                throw new IOException("The specified file could not be encrypted.", SafeFileIo.EncryptionFailedHResult);
        };
    }

    public void Dispose()
    {
        SafeFileIo.TestFault = null;
        SafeFileIo.TestIsEncrypted = null;
        SafeFileIo.TestEncrypt = null;
        SafeFileIo.TestFaultPath = null;
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void IsEncryptionFailure_KnowsTheHResult_AlsoWrapped()
    {
        IOException efs = new("The specified file could not be encrypted.", unchecked((int)0x80071770));
        Assert.True(SafeFileIo.IsEncryptionFailure(efs));
        Assert.True(SafeFileIo.IsEncryptionFailure(new InvalidOperationException("outer", efs)));
        Assert.False(SafeFileIo.IsEncryptionFailure(new IOException("disk full", unchecked((int)0x80070070))));
    }

    [Fact]
    public void CopyFile_CopiesTheBytes_KeepsTheTime_AndNeverUsesFileCopy()
    {
        string source = Write("preset.xml", "<Device ProductID=\"1234\"/>");
        DateTime written = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, written);
        // File.Copy would fail like on the user's PC: the helper copies the bytes.
        faults.Add("copy");
        string backup = Path.Combine(folder, "Backups", "preset.xml.jpbak");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        SafeFileIo.CopyFile(source, backup, overwrite: true, ownTarget: true);
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(backup));
        Assert.Equal(written, File.GetLastWriteTimeUtc(backup));
        Assert.Equal((FileAttributes)0, File.GetAttributes(backup) & FileAttributes.Encrypted);

        // Overwrite: a shorter file leaves nothing of the longer one behind; a hidden target is written too.
        File.SetAttributes(backup, FileAttributes.Hidden);
        File.WriteAllText(source, "x");
        SafeFileIo.CopyFile(source, backup, overwrite: true, ownTarget: true);
        Assert.Equal("x", File.ReadAllText(backup));
        Assert.Throws<IOException>(() => SafeFileIo.CopyFile(source, backup, overwrite: false, ownTarget: true));
    }

    [Fact]
    public void ReplaceFile_FallsBackToWritingTheBytes_WhenWindowsCannotCarryTheEncryption()
    {
        string path = Write("controls.ini", "old");
        string temp = Write("controls.ini.jptmp", "new");
        faults.Add("replace");
        SafeFileIo.ReplaceFile(temp, path);
        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(temp));

        // A new file: moved; when the move fails the same way, written.
        string fresh = Path.Combine(folder, "fresh.ini");
        temp = Write("fresh.ini.jptmp", "first");
        faults.Add("move");
        SafeFileIo.ReplaceFile(temp, fresh);
        Assert.Equal("first", File.ReadAllText(fresh));
        Assert.False(File.Exists(temp));

        // Without faults: File.Replace as before.
        faults.Clear();
        temp = Write("controls.ini.jptmp", "newer");
        SafeFileIo.ReplaceFile(temp, path);
        Assert.Equal("newer", File.ReadAllText(path));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void CopyOverAndDelete_FallsBack_AndLeavesNoTemp()
    {
        string path = Write("joystick.xml", "old");
        string temp = Write("joystick.xml.jptmp", "new");
        faults.Add("copy");
        SafeFileIo.CopyOverAndDelete(temp, path);
        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void ATargetThatCannotBeWritten_GetsAPlainMessage_AndKeepsItsContent()
    {
        string path = Write("profile.xml", "the game's file");
        string temp = Write("profile.xml.jptmp", "new");
        faults.AddRange(["replace", "stream"]);
        IOException ex = Assert.ThrowsAny<IOException>(() => SafeFileIo.ReplaceFile(temp, path));
        Assert.True(SafeFileIo.IsEncryptionFailure(ex));
        Assert.Contains("encrypted (EFS)", ex.Message);
        Assert.Contains(path, ex.Message);
        Assert.Contains("Encrypt contents", ex.Message);
        Assert.Equal("the game's file", File.ReadAllText(path));

        // A backup into JoyPro's own folder retries once after taking the mark off the folder, then says the same.
        string source = Write("preset.xml", "p");
        faults.Remove("replace");
        IOException copy = Assert.ThrowsAny<IOException>(() => SafeFileIo.CopyFile(source, Path.Combine(folder, "b.jpbak"), overwrite: true, ownTarget: true));
        Assert.Contains("encrypted (EFS)", copy.Message);
    }

    [Fact]
    public void TheEnvironmentFault_WorksForTheEngineSmokeTest()
    {
        string? old = Environment.GetEnvironmentVariable(SafeFileIo.FaultVariable);
        try
        {
            Environment.SetEnvironmentVariable(SafeFileIo.FaultVariable, "replace");
            string path = Write("a.ini", "old");
            string temp = Write("a.ini.jptmp", "new");
            SafeFileIo.ReplaceFile(temp, path);
            Assert.Equal("new", File.ReadAllText(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SafeFileIo.FaultVariable, old);
        }
    }

    // A real file with the Encrypted attribute (EFS) needs the user's EFS certificate; JoyPro's tests never create
    // one. ClearEncrypted on a plain file and folder does nothing.
    [Fact]
    public void ClearEncrypted_LeavesPlainFilesAlone()
    {
        string path = Write("plain.txt", "x");
        SafeFileIo.ClearEncrypted(path);
        SafeFileIo.ClearEncrypted(folder);
        SafeFileIo.ClearEncrypted(Path.Combine(folder, "missing.txt"));
        Assert.Equal("x", File.ReadAllText(path));
    }

    // 2.2.1 report (MSFS 2024, Microsoft Store on another drive): "The APPX file can not be accessed because it is not
    // encrypted as expected." (409). Recognized like 6000, with its own plain message.
    [Fact]
    public void AppxNotEncrypted_IsRecognized_AndExplained()
    {
        IOException appx = new("The APPX file can not be accessed because it is not encrypted as expected.", unchecked((int)0x80070199));
        Assert.True(SafeFileIo.IsEncryptionFailure(appx));
        Assert.True(SafeFileIo.IsAppxNotEncrypted(new InvalidOperationException("outer", appx)));
        Assert.False(SafeFileIo.IsAppxNotEncrypted(new IOException("efs", SafeFileIo.EncryptionFailedHResult)));
        IOException explained = SafeFileIo.Explain(appx, @"C:\x\preset.xml");
        Assert.Equal(SafeFileIo.AppxNotEncryptedHResult, explained.HResult);
        Assert.Contains("keeps its data encrypted on this drive", explained.Message);
        Assert.Contains(@"C:\x\preset.xml", explained.Message);
    }

    // A game folder that is encrypted: JoyPro's temp next to the game file keeps its encryption (never File.Decrypt),
    // so the written file stays encrypted. In a plain folder an encrypted temp still loses the mark.
    [Fact]
    public void EncryptedGameFolder_TempIsNeverDecrypted()
    {
        HashSet<string> encrypted = new(StringComparer.OrdinalIgnoreCase) { folder };
        List<string> decrypted = [];
        SafeFileIo.TestIsEncrypted = path => encrypted.Contains(path) || encrypted.Contains(Path.GetDirectoryName(path)!);
        SafeFileIo.TestFaultPath = (operation, path) =>
        {
            if (operation == "decrypt")
            {
                decrypted.Add(path);
                throw new IOException("test: no real File.Decrypt");
            }
        };
        string path = Write("preset.xml", "old");
        string temp = Write("preset.xml.jptmp", "new");
        SafeFileIo.ReplaceFile(temp, path);
        Assert.Equal("new", File.ReadAllText(path));
        temp = Write("preset.xml.jptmp", "newer");
        SafeFileIo.CopyOverAndDelete(temp, path);
        Assert.Equal("newer", File.ReadAllText(path));
        Assert.Empty(decrypted);

        // A plain folder: an encrypted temp (made elsewhere) loses its mark before it takes the file's place.
        encrypted.Clear();
        temp = Write("preset.xml.jptmp", "plain");
        encrypted.Add(temp);
        SafeFileIo.ReplaceFile(temp, path);
        Assert.Equal(new[] { temp }, decrypted);
    }

    // The ".jpbak" next to a game file: File.Copy first (keeps the encryption of an encrypted game folder), bytes
    // only when Windows cannot encrypt the target; the game's file and folder are never decrypted.
    [Fact]
    public void CopyBesideGameFile_UsesFileCopy_ThenTheBytes_AndNeverDecrypts()
    {
        List<string> operations = [];
        SafeFileIo.TestFault = operation =>
        {
            operations.Add(operation);
            if (faults.Contains(operation))
                throw new IOException("The specified file could not be encrypted.", SafeFileIo.EncryptionFailedHResult);
        };
        SafeFileIo.TestFaultPath = (operation, path) => operations.Add(operation + ":" + path);
        string source = Write("controls.xml", "game");
        DateTime written = new(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, written);
        SafeFileIo.CopyBesideGameFile(source, source + ".jpbak");
        Assert.Equal("game", File.ReadAllText(source + ".jpbak"));
        Assert.Equal(new[] { "copy" }, operations);

        operations.Clear();
        faults.Add("copy");
        File.WriteAllText(source, "game 2");
        File.SetLastWriteTimeUtc(source, written);
        SafeFileIo.CopyBesideGameFile(source, source + ".jpbak");
        Assert.Equal("game 2", File.ReadAllText(source + ".jpbak"));
        Assert.Equal(new[] { "copy", "stream" }, operations);
        Assert.Equal(written, File.GetLastWriteTimeUtc(source + ".jpbak"));
    }

    // The repair of JoyPro's MSFS package (JoyPro 2.1.1 to 2.2.1 removed its encryption): in an encrypted Community
    // folder every folder and file that lost the mark is encrypted again; a file Windows does not encrypt is written
    // again (it takes the folder's mark); one that cannot even be read is moved out of the way and reported. Nothing is
    // decrypted, and a plain Community folder is left as it is.
    [Fact]
    public void MatchFolderEncryption_ReEncryptsWhatLostTheMark()
    {
        string community = Path.Combine(folder, "Community2024");
        string package = Path.Combine(community, "joypro-input-profiles");
        string airplane = Path.Combine(package, "InputProfiles", "Categories", "Airplane");
        Directory.CreateDirectory(airplane);
        string ok = Path.Combine(airplane, "JoyPro_A_00000001_2024_Planes.xml");
        string stuck = Path.Combine(airplane, "JoyPro_B_00000002_2024_Planes.xml");
        string unreadable = Path.Combine(airplane, "JoyPro_C_00000003_2024_Planes.xml");
        string layout = Path.Combine(package, "layout.json");
        File.WriteAllText(ok, "a");
        File.WriteAllText(stuck, "b");
        File.WriteAllText(unreadable, "c");
        File.WriteAllText(layout, "{}");
        string quarantine = Path.Combine(folder, "JoyProData", "Backups", "MSFS", "package-1");

        // Plain Community folder: nothing happens.
        List<string> done = [];
        List<string> lost = [];
        List<string> operations = [];
        SafeFileIo.TestFaultPath = (operation, path) => operations.Add(operation + ":" + path);
        SafeFileIo.TestIsEncrypted = _ => false;
        SafeFileIo.MatchFolderEncryption(package, quarantine, done, lost);
        Assert.Empty(done);
        Assert.Empty(operations);

        // Encrypted Community folder; the package lost the mark everywhere.
        HashSet<string> encrypted = new(StringComparer.OrdinalIgnoreCase) { community };
        HashSet<string> recreated = new(StringComparer.OrdinalIgnoreCase);
        SafeFileIo.TestIsEncrypted = path => encrypted.Contains(path) || recreated.Contains(path);
        SafeFileIo.TestEncrypt = path =>
        {
            if (path == stuck || path == unreadable)
                throw new IOException("Access is denied.");
            encrypted.Add(path);
        };
        SafeFileIo.TestFaultPath = (operation, path) =>
        {
            operations.Add(operation + ":" + path);
            if (operation == "read" && path == unreadable)
                throw new IOException("The APPX file can not be accessed because it is not encrypted as expected.", SafeFileIo.AppxNotEncryptedHResult);
            if (operation == "move" && path == unreadable)
                throw new IOException("test: no move across drives");
            if (operation == "delete" && path == stuck)
                recreated.Add(path);
        };
        SafeFileIo.MatchFolderEncryption(package, quarantine, done, lost);

        Assert.Contains(package, encrypted);
        Assert.Contains(airplane, encrypted);
        Assert.Contains(ok, encrypted);
        Assert.Contains(layout, encrypted);
        Assert.Equal("a", File.ReadAllText(ok));
        Assert.Equal("b", File.ReadAllText(stuck));
        Assert.Contains(done, d => d.StartsWith("wrote again", StringComparison.Ordinal) && d.EndsWith(stuck, StringComparison.Ordinal));
        Assert.False(File.Exists(unreadable));
        Assert.Equal(new[] { unreadable }, lost);
        Assert.DoesNotContain(operations, o => o.StartsWith("decrypt", StringComparison.Ordinal));

        // A second run finds nothing left to do.
        done.Clear();
        lost.Clear();
        SafeFileIo.MatchFolderEncryption(package, quarantine, done, lost);
        Assert.Empty(done);
        Assert.Empty(lost);
    }

    // With real EFS (opt-in, JOYPRO_TEST_REAL_EFS=1: File.Encrypt makes an EFS certificate for the Windows user when
    // there is none, so the normal test run never calls it): a file JoyPro writes into an encrypted folder is
    // encrypted, and the repair encrypts a file that lost the mark.
    [Fact]
    public void RealEfs_WrittenFilesMatchTheFolder_OptIn()
    {
        if (Environment.GetEnvironmentVariable("JOYPRO_TEST_REAL_EFS") != "1")
            return;
        string community = Path.Combine(folder, "real", "Community2024");
        string package = Path.Combine(community, "joypro-input-profiles");
        Directory.CreateDirectory(package);
        try
        {
            File.Encrypt(community);
            File.Encrypt(package);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return; // EFS is not available here.
        }
        string path = Path.Combine(package, "preset.xml");
        File.WriteAllText(path, "old");
        Assert.True(SafeFileIo.IsEncrypted(path));
        string temp = Path.Combine(package, "preset.xml.jptmp");
        File.WriteAllText(temp, "new");
        SafeFileIo.ReplaceFile(temp, path);
        Assert.Equal("new", File.ReadAllText(path));
        Assert.True(SafeFileIo.IsEncrypted(path));
        SafeFileIo.CopyBesideGameFile(path, path + ".jpbak");
        Assert.True(SafeFileIo.IsEncrypted(path + ".jpbak"));

        File.Decrypt(path);
        File.Decrypt(package);
        Assert.False(SafeFileIo.IsEncrypted(path));
        List<string> done = [];
        List<string> lost = [];
        SafeFileIo.MatchFolderEncryption(package, Path.Combine(folder, "real", "quarantine"), done, lost);
        Assert.True(SafeFileIo.IsEncrypted(package));
        Assert.True(SafeFileIo.IsEncrypted(path));
        Assert.Empty(lost);
        Assert.Equal("new", File.ReadAllText(path));
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }
}
