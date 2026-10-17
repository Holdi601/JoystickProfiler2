using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// Entries of the open relation that look wrong (engine relations suggest: an old auto-fill put them there): the relation
// editor shows them above its suggestions with Move (to the relation of their function) and Remove. What fits the
// relation is in MainWindowViewModel.RelationBuilder.cs.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<RelationMisfitItem> RelationMisfits { get; } = [];

    public bool HasRelationMisfits => RelationMisfits.Count > 0;

    private static List<string> ReadStringArray(JsonElement item, string name)
    {
        List<string> values = [];
        if (item.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement value in list.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String)
                    values.Add(value.GetString() ?? string.Empty);
            }
        }
        return values;
    }

    [RelayCommand]
    private async Task MoveMisfitAsync(RelationMisfitItem? item)
    {
        if (item != null && item.CanMove)
            await ApplyMisfitAsync(item, ["--move", item.NodeSpec + "=>" + item.MoveTo], T("moved to '{0}'.", item.MoveTo));
    }

    [RelayCommand]
    private async Task RemoveMisfitAsync(RelationMisfitItem? item)
    {
        if (item != null)
            await ApplyMisfitAsync(item, ["--remove", item.NodeSpec], "removed.");
    }

    private async Task ApplyMisfitAsync(RelationMisfitItem item, List<string> option, string done)
    {
        RelationMisfits.Remove(item);
        OnPropertyChanged(nameof(HasRelationMisfits));
        await ApplyToBuilderRelationAsync(option, T("Fixing '{0}'...", item.Title), "'" + item.Title + "' " + done);
    }
}
