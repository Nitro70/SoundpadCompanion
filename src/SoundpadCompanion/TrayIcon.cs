using System.Drawing;
using Forms = System.Windows.Forms;

namespace SoundpadCompanion;

/// <summary>The notification-area icon: left-click opens Settings, right-click has the menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action openSearch, Action openSettings, Action quit)
    {
        var menu = new Forms.ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        var search = new Forms.ToolStripMenuItem("Search sounds", null, (_, _) => openSearch());
        search.Font = new Font(search.Font, FontStyle.Bold);
        menu.Items.Add(search);
        menu.Items.Add("Settings", null, (_, _) => openSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());

        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcon.LoadTrayIcon(),
            Text = "Soundpad Companion",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                openSettings();
        };
    }

    /// <summary>Hover text (Windows caps it at 63 characters).</summary>
    public string Tooltip
    {
        set => _icon.Text = value.Length > 63 ? value[..63] : value;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color.FromArgb(0xE6, 0xED, 0xF2);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly Color Surface = Color.FromArgb(0x16, 0x19, 0x1C);
        private static readonly Color Hover = Color.FromArgb(0x12, 0x32, 0x3A);
        private static readonly Color Line = Color.FromArgb(0x2A, 0x2F, 0x35);

        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
    }
}
