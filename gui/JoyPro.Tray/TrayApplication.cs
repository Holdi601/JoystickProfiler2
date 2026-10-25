using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using JoyPro.Watch;

namespace JoyPro.Tray;

// The tray icon and its menu around TrayWatcherHost: Open JoyPro2, Review ... (when a game changed controls),
// Watching game configs, Exit. Notifications are balloons (Windows shows them as toasts; they never take the focus);
// clicking one opens the review in JoyPro2. Nothing here runs while idle: no timer, no window but WinForms' hidden
// marshaling window.
internal sealed class TrayApplication : ApplicationContext, ITrayView
{
    private readonly WindowsFormsSynchronizationContext context;
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem openItem;
    private readonly ToolStripMenuItem reviewItem;
    private readonly ToolStripMenuItem watchingItem;
    private readonly ToolStripMenuItem exitItem;
    private readonly Icon iconImage;
    private readonly TrayWatcherHost host;
    private bool closed;

    public TrayApplication(InstanceNames names, string guiExe, string engineExe)
    {
        context = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);

        openItem = new ToolStripMenuItem(WatchText.T("Open JoyPro2"), null, (_, _) => host!.Open());
        reviewItem = new ToolStripMenuItem(WatchText.T("Review"), null, (_, _) => host!.Review()) { Visible = false };
        watchingItem = new ToolStripMenuItem("Watching game configs", null, (_, _) => host!.ToggleWatching()) { CheckOnClick = false };
        exitItem = new ToolStripMenuItem(WatchText.T("Exit"), null, (_, _) => host!.Exit());
        menu = new ContextMenuStrip();
        menu.Items.AddRange([openItem, reviewItem, watchingItem, new ToolStripSeparator(), exitItem]);
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);

        using (System.IO.Stream? stream = typeof(TrayApplication).Assembly.GetManifestResourceStream("JoyPro.Tray.icon.ico"))
            iconImage = stream != null ? new Icon(stream, SystemInformation.SmallIconSize) : (Icon)SystemIcons.Application.Clone();
        icon = new NotifyIcon
        {
            Icon = iconImage,
            Text = "JoyPro2",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.MouseClick += OnIconClick;
        icon.BalloonTipClicked += OnBalloonClicked;

        host = new TrayWatcherHost(
            new ProcessWatchEngine(engineExe),
            new UiSettingsFileStore(JoyProPaths.UiSettingsFile(names.SettingsFolder)),
            new GuiPresence(names),
            new ProcessJoyProLauncher(guiExe),
            this,
            () => new GameConfigWatcher(),
            action => context.Post(_ => action(), null),
            TrimMemory,
            usageTailerFactory: folder => new UsageLogTailer(folder));
        // Started inside the message loop, so the engine calls continue on this thread.
        context.Post(_ => _ = host.StartAsync(), null);
    }

    public void Show(TrayViewState state)
    {
        if (closed)
            return;
        icon.Text = state.ToolTip;
        reviewItem.Visible = state.ReviewText != null;
        reviewItem.Text = state.ReviewText ?? WatchText.T("Review");
        watchingItem.Text = state.WatchingText;
        watchingItem.Checked = state.WatchingChecked;
        watchingItem.Enabled = state.WatchingEnabled;
    }

    public void Notify(string title, string text)
    {
        if (!closed)
            icon.ShowBalloonTip(15000, title, text, ToolTipIcon.Info);
    }

    public void Close()
    {
        if (closed)
            return;
        closed = true;
        icon.Visible = false;
        ExitThread();
    }

    private void OnIconClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        if (host.Pending.Count > 0)
            host.Review();
        else
            host.Open();
    }

    private void OnBalloonClicked(object? sender, EventArgs e) => host.Review();

    // After startup and after each check: give the pages back to Windows (a few MB stay in use).
    private static void TrimMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using Process self = Process.GetCurrentProcess();
        SetProcessWorkingSetSize(self.Handle, -1, -1);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            host.Dispose();
            icon.MouseClick -= OnIconClick;
            icon.BalloonTipClicked -= OnBalloonClicked;
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            iconImage.Dispose();
        }
        base.Dispose(disposing);
    }
}
