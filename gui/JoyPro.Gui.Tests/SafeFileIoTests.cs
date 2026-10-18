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

    private string Write(string name, string text)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }
}
