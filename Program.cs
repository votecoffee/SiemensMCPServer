using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SiemensMCPServer
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            IList<string> compatibleVersions = TiaPortalAssemblyResolver.GetCompatibleEngineeringVersions();
            string requestedVersion = GetRequestedVersion(args);
            string selectedVersion = ResolveRequestedVersion(requestedVersion, compatibleVersions);

            if (!string.IsNullOrEmpty(requestedVersion) && selectedVersion == null)
            {
                MessageBox.Show(
                    "TIA Portal " + requestedVersion + " is not installed with a compatible Openness API for this build." +
                    Environment.NewLine + Environment.NewLine +
                    BuildCompatibilityMessage(compatibleVersions),
                    "TIA Portal version unavailable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            if (selectedVersion == null)
            {
                if (compatibleVersions.Count == 0)
                {
                    MessageBox.Show(
                        "No compatible TIA Portal Openness installation was found." +
                        Environment.NewLine + Environment.NewLine +
                        "This build references Openness API " +
                        (TiaPortalAssemblyResolver.ReferencedApiVersion ?? "<unknown>") + "." +
                        Environment.NewLine +
                        "Install a TIA Portal version that provides this API, or rebuild the project against an API available on this computer.",
                        "TIA Portal Openness not found",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                selectedVersion = compatibleVersions.Count == 1
                    ? compatibleVersions[0]
                    : SelectEngineeringVersion(compatibleVersions);

                if (selectedVersion == null)
                    return;
            }

            TiaPortalAssemblyResolver.Initialize(selectedVersion);
            RunMainForm();
        }

        private static void RunMainForm()
        {
            // Keep Siemens.Engineering types out of Main so AssemblyResolve is registered before MainForm is loaded.
            Application.Run(new MainForm());
        }

        private static string GetRequestedVersion(string[] args)
        {
            foreach (string arg in args ?? new string[0])
            {
                const string prefix = "--tia-version=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(prefix.Length).Trim();

                const string slashPrefix = "/tia-version:";
                if (arg.StartsWith(slashPrefix, StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(slashPrefix.Length).Trim();
            }

            return Environment.GetEnvironmentVariable("TIA_PORTAL_VERSION");
        }

        private static string ResolveRequestedVersion(string requested, IList<string> compatibleVersions)
        {
            if (string.IsNullOrWhiteSpace(requested))
                return null;

            string normalized = requested.Trim();
            if (!normalized.StartsWith("V", StringComparison.OrdinalIgnoreCase))
                normalized = "V" + normalized;

            return compatibleVersions.FirstOrDefault(
                version => string.Equals(version, normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static string SelectEngineeringVersion(IList<string> versions)
        {
            using (var form = new Form())
            using (var combo = new ComboBox())
            using (var ok = new Button())
            using (var cancel = new Button())
            using (var label = new Label())
            {
                form.Text = "Select TIA Portal version";
                form.StartPosition = FormStartPosition.CenterScreen;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ClientSize = new Size(430, 140);

                label.Text = "Choose the installed TIA Portal version to use:";
                label.AutoSize = true;
                label.Location = new Point(14, 16);

                combo.DropDownStyle = ComboBoxStyle.DropDownList;
                combo.Location = new Point(17, 45);
                combo.Width = 395;
                foreach (string version in versions)
                    combo.Items.Add(version);
                combo.SelectedIndex = 0;

                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.Location = new Point(256, 91);
                ok.Width = 75;

                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Location = new Point(337, 91);
                cancel.Width = 75;

                form.Controls.Add(label);
                form.Controls.Add(combo);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                return form.ShowDialog() == DialogResult.OK
                    ? combo.SelectedItem as string
                    : null;
            }
        }

        private static string BuildCompatibilityMessage(IList<string> compatibleVersions)
        {
            string api = TiaPortalAssemblyResolver.ReferencedApiVersion ?? "<unknown>";
            if (compatibleVersions.Count == 0)
                return "Referenced Openness API: " + api + Environment.NewLine + "Compatible installed TIA Portal versions: none";

            return "Referenced Openness API: " + api + Environment.NewLine +
                   "Compatible installed TIA Portal versions: " + string.Join(", ", compatibleVersions);
        }
    }
}
