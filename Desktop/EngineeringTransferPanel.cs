using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace SiemensMCPServer
{
    internal sealed class EngineeringTransferPanel : UserControl
    {
        private readonly TreeView tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        private readonly Button exportSelected = new Button { Text = "Export selected resources", AutoSize = true };
        private readonly Button exportAll = new Button { Text = "Export target resources", AutoSize = true };
        private readonly Button importNative = new Button { Text = "Import native resource folder...", AutoSize = true };
        private readonly Button importBundle = new Button { Text = "Import export bundle...", AutoSize = true };
        private readonly CheckBox overwrite = new CheckBox { Text = "Overwrite existing objects", AutoSize = true, Margin = new Padding(12, 7, 3, 3) };
        private readonly Label scope = new Label { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8), AutoEllipsis = true };
        private readonly TextBox details = new TextBox { Dock = DockStyle.Bottom, Height = 78, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
        private readonly Action<string> log;
        private readonly SafeguardStore safeguards;
        private readonly Action<string, Exception> error;
        private EngineeringTransferSession? session;
        private bool canImport;

        internal EngineeringTransferPanel(Action<string> log, Action<string, Exception> error, SafeguardStore safeguards)
        {
            this.log = log; this.error = error; this.safeguards = safeguards;
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70, AutoScroll = true, Padding = new Padding(6), WrapContents = true };
            actions.Controls.AddRange(new Control[] { exportSelected, exportAll, importNative, importBundle, overwrite });
            Controls.Add(tree); Controls.Add(details); Controls.Add(actions); Controls.Add(scope);
            tree.BeforeExpand += (s, e) => { if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag == null) Run(() => Populate(e.Node)); };
            tree.AfterSelect += (s, e) => Run(ReadSelection);
            exportSelected.Click += (s, e) => Export(false);
            exportAll.Click += (s, e) => Export(true);
            importNative.Click += (s, e) => Import(false);
            importBundle.Click += (s, e) => Import(true);
            SelectSession(null);
        }
        internal void SelectSession(EngineeringTransferSession? value)
        {
            session = value; overwrite.Checked = false;
            scope.Text = value == null ? "Select a Unified HMI or PLC target."
                : value.Family == "plc" ? "PLC: offline XML blocks, data types and tag tables; external sources are browseable. Transfers require a verified Offline state."
                : "Unified: native XML/YAML/JS transfers depend on the installed API. Objects without native transfer methods remain browseable; V19 has no screen or text-list transfer route.";
            RefreshResources();
        }
        internal void RefreshResources()
        {
            tree.Nodes.Clear(); details.Clear(); canImport = false;
            if (session != null)
            {
                var root = new TreeNode(session.TargetLabel) { Tag = "" };
                root.Nodes.Add(new TreeNode("Loading...")); tree.Nodes.Add(root);
                Run(() => { Populate(root); root.Expand(); tree.SelectedNode = root; });
            }
            UpdateActions();
        }
        private string SelectedPath => tree.SelectedNode?.Tag as string ?? "";
        private void Populate(TreeNode node)
        {
            var state = session!.Read((string)node.Tag);
            if (state.Errors.Length != 0) throw new InvalidOperationException(string.Join("; ", state.Errors));
            node.Nodes.Clear();
            foreach (var child in state.Children)
            {
                string part = child.Label;
                string label = part.Length > 2 ? Uri.UnescapeDataString(part.Substring(2)) : part;
                var item = new TreeNode(label + "  (" + child.NativeType.Split('.').Last() + ")") { Tag = child.Path };
                item.Nodes.Add(new TreeNode("Loading...")); node.Nodes.Add(item);
            }
        }
        private void ReadSelection()
        {
            canImport = false;
            if (session == null || tree.SelectedNode?.Tag == null) { UpdateActions(); return; }
            var state = session.Read(SelectedPath);
            canImport = state.CanImport && state.Errors.Length == 0;
            details.Text = "Path: " + (SelectedPath == "" ? "(target root)" : SelectedPath) + Environment.NewLine
                + "Native type: " + state.NativeType + Environment.NewLine
                + "Native export: " + (state.CanExport ? "available" : "not exposed; export traverses supported descendants")
                + "; native import: " + (canImport ? "available" : "not exposed on this selection; select an importable composition")
                + (state.Errors.Length == 0 ? "" : Environment.NewLine + string.Join("; ", state.Errors));
            UpdateActions();
        }
        internal void RefreshPolicy() => UpdateActions();
        private void UpdateActions()
        {
            bool selected = session != null && tree.SelectedNode?.Tag != null;
            exportSelected.Enabled = selected; exportAll.Enabled = session != null;
            bool writes = false, allowOverwrite = false;
            try { var policy = safeguards.Current(); policy.Require(GuardedOperation.Apply); if (session != null) policy.RequireProject(session.ProjectPath); writes = true; allowOverwrite = policy.AllowOverwrite; } catch { }
            importNative.Enabled = selected && canImport && writes; importBundle.Enabled = selected && writes;
            overwrite.Enabled = session != null && writes && allowOverwrite;
            if (!allowOverwrite) overwrite.Checked = false;
        }
        private void Export(bool all)
        {
            if (session == null) return;
            using (var dialog = new FolderBrowserDialog { Description = "Choose the parent folder for the native resource export" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                Run(() => {
                    string directory = session.Export(all ? "" : SelectedPath, dialog.SelectedPath);
                    using (var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "export-index.json"))))
                    {
                        int count = index.RootElement.GetProperty("entries").GetArrayLength();
                        int failures = index.RootElement.GetProperty("failures").GetArrayLength();
                        log("Exported " + count + " native resource(s), " + failures + " failure(s) -> " + directory);
                        if (count == 0 || failures != 0) MessageBox.Show(this,
                            "Native resources exported: " + count + ". Failures: " + failures + ". Review export-index.json. Objects without native export methods have metadata snapshots only.",
                            "Export scope", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                });
            }
        }
        private void Import(bool bundle)
        {
            if (session == null) return;
            using (var dialog = new FolderBrowserDialog { Description = bundle ? "Select the export root containing export-index.json" : "Select one native XML/YML resource folder, including all companions" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                Run(() => {
                    var plan = session.PrepareImport(SelectedPath, dialog.SelectedPath, overwrite.Checked, bundle);
                    string review = "Project: " + session.ProjectPath + Environment.NewLine + "Target: " + session.TargetLabel + Environment.NewLine
                        + "Destination: " + (SelectedPath == "" ? "(target root)" : SelectedPath) + Environment.NewLine
                        + "Native resources: " + plan.ResourceCount + Environment.NewLine + "Overwrite: " + plan.Overwrite + Environment.NewLine
                        + "Staged SHA-256: " + plan.FilesHash + Environment.NewLine + "Baseline and staged files: " + plan.Directory + Environment.NewLine + Environment.NewLine
                        + (session.Family == "unified" ? "Unified uses its native replacement scope; affected collections may be replaced." + Environment.NewLine : "")
                        + "Apply this import? The project will remain unsaved. Inspect the result and compile in TIA before saving.";
                    log("Prepared import; retained baseline and candidate -> " + plan.Directory);
                    if (safeguards.Current().ConfirmDesktopImports && MessageBox.Show(this, review, "Review engineering import", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                    session.Apply(plan);
                    log("Native import accepted. Inspect after-export in " + plan.Directory + "; compile in TIA, then Save project explicitly.");
                    RefreshResources();
                });
            }
        }
        private void Run(Action action)
        {
            bool previous = UseWaitCursor; UseWaitCursor = true;
            try { action(); }
            catch (Exception ex) { error("Engineering resource operation failed.", ex.GetBaseException()); }
            finally { UseWaitCursor = previous; UpdateActions(); }
        }
    }
}
