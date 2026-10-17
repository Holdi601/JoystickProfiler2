namespace JoyPro.Gui.Models;

// One backup set of game config files (engine backups list): taken before an export or a restore.
public sealed class GameBackupItem
{
    public GameBackupItem(string id, string game, string created, int fileCount, string command)
    {
        Id = id;
        Game = game;
        Created = created;
        FileCount = fileCount;
        Command = command;
    }

    public string Id { get; }

    public string Game { get; }

    public string Created { get; }

    public int FileCount { get; }

    public string Command { get; }

    public string Kind => Command.StartsWith("restore ") ? "before restore" : "before export";
}
