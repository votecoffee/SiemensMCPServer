using System;
using System.Drawing;
using System.Windows.Forms;

namespace SiemensMCPServer
{
    internal sealed class SafeguardsForm : Form
    {
        private readonly PropertyGrid grid = new PropertyGrid { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true,
            PropertySort = PropertySort.Categorized, Name = "safeguardsGrid" };
        private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8), AutoEllipsis = true };
        private readonly SafeguardStore store;
        private SafeguardSnapshot snapshot = new SafeguardSnapshot();
        private readonly Button save = new Button { Text = "Save settings", AutoSize = true, Name = "saveSettings" };

        internal SafeguardsForm(SafeguardStore store)
        {
            this.store = store;
            Text = "SiemensMCPServer - Safeguards";
            Size = new Size(920, 790); MinimumSize = new Size(700, 580);
            StartPosition = FormStartPosition.CenterParent;
            var header = new Label { Dock = DockStyle.Top, Height = 96, Padding = new Padding(10), Text =
                "Shared desktop and MCP policy. Select a setting to read its description below.\r\n"
                + "Permissions, confirmations and preview limits are checked for subsequent operations. Request limits and the MCP artifact folder require a host restart.\r\n"
                + "Always-enforced checks are listed for reference. Legacy Comfort imports keep their documented per-object behavior.\r\nSettings: " + store.FilePath };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45, Padding = new Padding(6), FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
            var reload = new Button { Text = "Reload saved", AutoSize = true };
            var defaults = new Button { Text = "Load defaults", AutoSize = true };
            buttons.Controls.AddRange(new Control[] { close, save, reload, defaults });
            Controls.Add(grid); Controls.Add(status); Controls.Add(buttons); Controls.Add(header);
            CancelButton = close;
            reload.Click += (s, e) => LoadSaved();
            defaults.Click += (s, e) => { grid.SelectedObject = new SafeguardSettings(); status.Text = "Defaults loaded for review. Save settings to apply them."; };
            save.Click += (s, e) => SaveSettings();
            LoadSaved();
        }
        private void LoadSaved()
        {
            try {
                snapshot = store.Load(); grid.SelectedObject = snapshot.Settings; save.Enabled = true;
                status.Text = snapshot.Hash == "absent" ? "Using compatibility defaults. No settings file has been saved yet. MCP still requires --allow-writes." : "Loaded saved policy. Changes are local to this Windows user/settings file.";
            } catch (Exception ex) { save.Enabled = false; grid.SelectedObject = null; status.Text = "Cannot load policy: " + ex.Message + " Correct or restore the file, then Reload saved. No default policy has replaced it."; }
        }
        private void SaveSettings()
        {
            try {
                // End the active editor before reading the model.
                save.Focus();
                snapshot = store.Save((SafeguardSettings)grid.SelectedObject, snapshot.Hash);
                grid.SelectedObject = snapshot.Settings;
                status.Text = "Saved. Permissions and preview limits apply to subsequent operations; restart MCP to apply request limits/artifact location. Previous settings are retained in .previous.";
            } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Settings were not saved", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
