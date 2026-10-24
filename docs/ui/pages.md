# Pages in the bottom bar

Settings > **Pages in the bottom bar** lists the pages of the bar at the bottom of the window (Start, Relations, Binds,
Modifiers, Layout, Export, HUD, Force Feedback, Virtual Joystick, Cockpits, Settings, Advanced, Check, Output):

- Untick a page to hide its button. **Settings** cannot be hidden: it is the way back to this list.
- Drag a row by its handle, or use its arrows, to put the pages in your order (up = further left in the bar).
- **Reset to default** brings JoyPro's order back with every page shown.
- A hidden page still opens where another page links to it (for example "Open the Modifiers page", follow input, a
  search result). Its button shows in the bar while it is open, with the note "<page> is hidden in the bar and shows
  only while it is open." and **Show it always**. Output shows while its panel is open.

Kept in `ui-settings.json` (`navPageOrder`, `hiddenNavPages`); a page that a later version adds goes where it stands in
JoyPro's order. Code: `ViewModels/MainWindowViewModel.NavPages.cs`, the bar `Views/MainWindow.NavBar.cs`. Tests:
`gui/JoyPro.Gui.Tests/NavPagesTests.cs`.
