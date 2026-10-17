namespace JoyPro.Gui.Models;

public sealed class ValidationIssueItem
{
    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string ActionHint { get; set; } = string.Empty;
}

