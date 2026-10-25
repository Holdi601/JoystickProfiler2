namespace JoyPro.Gui.Models;

public sealed class ValidationIssueItem
{
    // Shown (in the language of the GUI).
    public string Type { get; set; } = string.Empty;

    // "Error" or "Warning" (never translated: the filter of the list).
    public string Kind { get; set; } = "Warning";

    public string Message { get; set; } = string.Empty;

    public string ActionHint { get; set; } = string.Empty;

    public bool IsError => Kind == "Error";
}
