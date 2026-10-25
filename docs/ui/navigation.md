# Back and Forward

The header has **Back** and **Forward** arrows next to Undo, like a browser. They move through the places you were,
not through changes of the profile (that is Undo / Redo / History).

- A place is the page, its sub-view (Layout: Device pictures / Cheat sheets; the relation editor's panel such as Add
  actions or Suggested; the Force Feedback tab), the open relation (Relations) or device (Layout), the search texts and
  filters of the lists (relations, binds, the Check panel's search, kind and order, the settings search), which Check
  cards and sections are open (and which settings sections on the Settings page), and the scroll positions of the page's
  lists.
- Changing the page puts the page you leave into the history. Jumps push first: **Open both relations** on a Check card,
  a relation opened from the aircraft check, the assistant or the missing-actions review, a settings tab that scrolls to
  its section, Device pictures / Cheat sheets, the relation editor's panels. So Back after **Open both relations**
  returns to the same Check list, scrolled to the same card.
- Back / Forward: the header's arrows (the tooltip names where they go), **Alt+Left** / **Alt+Right** (a text box keeps
  its keys), the mouse's back and forward buttons.
- At most 50 places; the same place twice in a row is kept once; a new place clears Forward. Selecting another relation
  in the list is not a place of its own (Back returns to the page as you left it).

Code: `Services/NavigationHistory.cs` (the history), `ViewModels/MainWindowViewModel.NavigationHistory.cs` (capture and
restore), `Views/MainWindow.Navigation.cs` (keys, mouse buttons, scroll positions: each list is found again by its place
in the page's tree and restored after layout). Tests: `gui/JoyPro.Gui.Tests/NavigationHistoryTests.cs`
(`JOYPRO_NAV_OUT=<folder>` keeps the renders).
