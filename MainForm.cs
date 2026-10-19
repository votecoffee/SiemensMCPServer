using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Screen;
using HmiScreen = Siemens.Engineering.Hmi.Screen.Screen;

namespace SiemensMCPServer
{
    public sealed class MainForm : Form
    {
        private readonly ComboBox _processCombo = new ComboBox();
        private readonly Button _refreshProcessesButton = new Button();
        private readonly Button _attachButton = new Button();
        private readonly ComboBox _hmiCombo = new ComboBox();
        private readonly Button _refreshScreensButton = new Button();
        private readonly TreeView _screenTree = new TreeView();
        private readonly Button _exportSelectedButton = new Button();
        private readonly Button _exportAllButton = new Button();
        private readonly Button _exportCompleteButton = new Button();
        private readonly Button _importCompleteButton = new Button();
        private readonly Button _importScreensOnlyButton = new Button();
        private readonly Button _importButton = new Button();
        private readonly CheckBox _overwriteCheck = new CheckBox();
        private readonly Button _saveProjectButton = new Button();
        private readonly TextBox _log = new TextBox();
        private readonly Label _projectLabel = new Label();

        private IList<TiaPortalProcess> _processes = new List<TiaPortalProcess>();
        private TiaPortal _portal;
        private Project _project;
        private FlowLayoutPanel _actions;
        private EngineeringTransferPanel _engineeringPanel;
        private SoftwareReference _activeTarget;
        private HmiTarget _activeHmi;

        public MainForm() : this(true) { }

        // Allows layout checks without enumerating processes or opening an Openness connection.
        internal MainForm(bool discoverProcesses)
        {
            Text = "SiemensMCPServer - TIA Portal " + TiaPortalAssemblyResolver.SelectedEngineeringVersion + " - HMI / PLC Import and Export";
            Width = 1220;
            Height = 760;
            MinimumSize = new Size(1050, 620);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            if (discoverProcesses) RefreshProcesses();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Disposing this object only releases our Openness session.
            // It does not close a TIA Portal instance that we attached to.
            if (_portal != null)
            {
                _portal.Dispose();
                _portal = null;
            }
            base.OnFormClosed(e);
        }

        private void BuildUi()
        {
            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 105,
                ColumnCount = 6,
                RowCount = 3,
                Padding = new Padding(8),
                AutoSize = false
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

            _processCombo.Dock = DockStyle.Fill;
            _processCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _refreshProcessesButton.Text = "Refresh TIA";
            _refreshProcessesButton.Dock = DockStyle.Fill;
            _refreshProcessesButton.Click += (s, e) => RefreshProcesses();
            _attachButton.Text = "Attach";
            _attachButton.Dock = DockStyle.Fill;
            _attachButton.Click += (s, e) => AttachToSelectedProcess();

            _hmiCombo.Dock = DockStyle.Fill;
            _hmiCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _hmiCombo.SelectedIndexChanged += (s, e) => SelectHmi();
            _refreshScreensButton.Text = "Refresh";
            _refreshScreensButton.Dock = DockStyle.Fill;
            _refreshScreensButton.Click += (s, e) => { if (_activeHmi != null) LoadScreenTree(); else _engineeringPanel.RefreshResources(); };

            _projectLabel.Dock = DockStyle.Fill;
            _projectLabel.AutoEllipsis = true;
            _projectLabel.TextAlign = ContentAlignment.MiddleLeft;

            top.Controls.Add(new Label { Text = "TIA instance:", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
            top.Controls.Add(_processCombo, 1, 0);
            top.SetColumnSpan(_processCombo, 3);
            top.Controls.Add(_refreshProcessesButton, 4, 0);
            top.Controls.Add(_attachButton, 5, 0);

            top.Controls.Add(new Label { Text = "HMI / PLC:", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
            top.Controls.Add(_hmiCombo, 1, 1);
            top.SetColumnSpan(_hmiCombo, 4);
            top.Controls.Add(_refreshScreensButton, 5, 1);

            top.Controls.Add(new Label { Text = "Project:", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 2);
            top.Controls.Add(_projectLabel, 1, 2);
            top.SetColumnSpan(_projectLabel, 5);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 82,
                Padding = new Padding(8, 6, 8, 6),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = true
            };
            _actions = actions;
            _exportSelectedButton.Text = "Export selected";
            _exportSelectedButton.AutoSize = true;
            _exportSelectedButton.Click += (s, e) => ExportSelected();

            _exportAllButton.Text = "Export all screens";
            _exportAllButton.AutoSize = true;
            _exportAllButton.Click += (s, e) => ExportAllScreens();

            _exportCompleteButton.Text = "Export Complete HMI";
            _exportCompleteButton.AutoSize = true;
            _exportCompleteButton.Click += (s, e) => ExportCompleteHmi();

            _importCompleteButton.Text = "Import Complete HMI";
            _importCompleteButton.AutoSize = true;
            _importCompleteButton.Click += (s, e) => ImportCompleteHmi();

            _importScreensOnlyButton.Text = "Import Screens Only";
            _importScreensOnlyButton.AutoSize = true;
            _importScreensOnlyButton.Click += (s, e) => ImportScreensOnly();

            _importButton.Text = "Import screen XML...";
            _importButton.AutoSize = true;
            _importButton.Click += (s, e) => ImportScreens();

            _overwriteCheck.Text = "Overwrite existing objects";
            _overwriteCheck.AutoSize = true;
            _overwriteCheck.Margin = new Padding(15, 7, 3, 3);
            _overwriteCheck.Checked = false;

            _saveProjectButton.Text = "Save project";
            _saveProjectButton.AutoSize = true;
            _saveProjectButton.Margin = new Padding(20, 3, 3, 3);
            _saveProjectButton.Click += (s, e) => SaveProject();

            actions.Controls.Add(_exportSelectedButton);
            actions.Controls.Add(_exportAllButton);
            actions.Controls.Add(_exportCompleteButton);
            actions.Controls.Add(_importCompleteButton);
            actions.Controls.Add(_importScreensOnlyButton);
            actions.Controls.Add(_importButton);
            actions.Controls.Add(_overwriteCheck);
            actions.Controls.Add(_saveProjectButton);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 395,
                Panel1MinSize = 200,
                Panel2MinSize = 120
            };

            _screenTree.Dock = DockStyle.Fill;
            _screenTree.HideSelection = false;
            _screenTree.AfterSelect += (s, e) => UpdateActionState();
            split.Panel1.Controls.Add(_screenTree);
            _engineeringPanel = new EngineeringTransferPanel(Log, ShowError) { Dock = DockStyle.Fill, Visible = false };
            split.Panel1.Controls.Add(_engineeringPanel);

            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Both;
            _log.ReadOnly = true;
            _log.WordWrap = false;
            _log.Font = new Font(FontFamily.GenericMonospace, 9f);
            split.Panel2.Controls.Add(_log);

            Controls.Add(split);
            Controls.Add(actions);
            Controls.Add(top);

            UpdateActionState();
        }

        private void RefreshProcesses()
        {
            try
            {
                _processes = TiaPortal.GetProcesses();
                _processCombo.Items.Clear();

                foreach (TiaPortalProcess process in _processes)
                {
                    string projectPath = process.ProjectPath == null ? "<no project open>" : process.ProjectPath.FullName;
                    _processCombo.Items.Add(new ProcessEntry(process, "PID " + process.Id + "  |  " + projectPath));
                }

                if (_processCombo.Items.Count > 0)
                    _processCombo.SelectedIndex = 0;

                Log("Found " + _processCombo.Items.Count + " running TIA Portal instance(s)." );
            }
            catch (Exception ex)
            {
                ShowError("Could not enumerate TIA Portal instances.", ex);
            }
        }

        private void AttachToSelectedProcess()
        {
            try
            {
                var selected = _processCombo.SelectedItem as ProcessEntry;
                if (selected == null)
                {
                    MessageBox.Show(this, "Start the selected TIA Portal version, open the project, then click Refresh TIA.", "No TIA instance", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (_portal != null)
                {
                    ClearAttachment();
                    _portal.Dispose();
                    _portal = null;
                }

                Log("Attaching to TIA Portal PID " + selected.Process.Id + "...");
                _portal = selected.Process.Attach();

                if (_portal.Projects.Count == 0)
                    throw new InvalidOperationException("The selected TIA Portal instance does not have an open project.");

                _project = _portal.Projects[0];
                _projectLabel.Text = _project.Path == null ? _project.Name : _project.Path.FullName;

                DiscoverHmis();
                Log("Attached to project: " + _projectLabel.Text);
            }
            catch (Exception ex)
            {
                ClearAttachment();
                ShowError("Could not attach to TIA Portal.", ex);
            }
        }

        private void DiscoverHmis()
        {
            _activeTarget = null;
            _activeHmi = null;
            _hmiCombo.Items.Clear();

            foreach (SoftwareReference entry in SoftwareCatalog.Discover(_project))
                _hmiCombo.Items.Add(new TargetEntry(entry));

            if (_hmiCombo.Items.Count > 0)
                _hmiCombo.SelectedIndex = 0;
            else
            {
                _activeHmi = null;
                _screenTree.Nodes.Clear();
                _engineeringPanel.SelectSession(null);
                Log("No supported HMI or PLC software was found in the project.");
            }

            UpdateActionState();
        }

        private void SelectHmi()
        {
            var selected = _hmiCombo.SelectedItem as TargetEntry;
            _activeTarget = selected == null ? null : selected.Reference;
            _activeHmi = _activeTarget == null ? null : _activeTarget.Software as HmiTarget;
            bool engineering = _activeTarget != null && _activeHmi == null;
            _engineeringPanel.Visible = engineering;
            _screenTree.Visible = !engineering;
            if (engineering)
            {
                _engineeringPanel.BringToFront();
                _engineeringPanel.SelectSession(new EngineeringTransferSession(_portal, _project, _activeTarget));
            }
            else { _engineeringPanel.SelectSession(null); LoadScreenTree(); }
            UpdateActionState();
        }

        private void ClearAttachment()
        {
            _project = null; _activeTarget = null; _activeHmi = null;
            _hmiCombo.Items.Clear(); _screenTree.Nodes.Clear(); _projectLabel.Text = "";
            _engineeringPanel.SelectSession(null);
            UpdateActionState();
        }

        private void LoadScreenTree()
        {
            _screenTree.BeginUpdate();
            try
            {
                _screenTree.Nodes.Clear();
                if (_activeHmi == null)
                    return;

                ScreenSystemFolder rootFolder = _activeHmi.ScreenFolder;
                var root = new TreeNode("Screens") { Tag = rootFolder };
                _screenTree.Nodes.Add(root);

                AddScreens(root, rootFolder.Screens);
                foreach (ScreenUserFolder folder in rootFolder.Folders)
                    AddUserFolder(root, folder);

                root.Expand();
                _screenTree.SelectedNode = root;
                Log("Loaded screen tree." );
            }
            catch (Exception ex)
            {
                ShowError("Could not read the HMI screen tree.", ex);
            }
            finally
            {
                _screenTree.EndUpdate();
                UpdateActionState();
            }
        }

        private void AddUserFolder(TreeNode parent, ScreenUserFolder folder)
        {
            var node = new TreeNode(folder.Name) { Tag = folder };
            parent.Nodes.Add(node);
            AddScreens(node, folder.Screens);
            foreach (ScreenUserFolder subFolder in folder.Folders)
                AddUserFolder(node, subFolder);
        }

        private static void AddScreens(TreeNode parent, ScreenComposition screens)
        {
            foreach (HmiScreen screen in screens)
                parent.Nodes.Add(new TreeNode(screen.Name) { Tag = screen });
        }

        private void ExportSelected()
        {
            if (_activeHmi == null || _screenTree.SelectedNode == null)
                return;

            try
            {
                HmiScreen screen = _screenTree.SelectedNode.Tag as HmiScreen;
                if (screen != null)
                {
                    using (var dialog = new SaveFileDialog())
                    {
                        dialog.Filter = "TIA screen XML (*.xml)|*.xml|All files (*.*)|*.*";
                        dialog.FileName = SafeName(screen.Name) + ".xml";
                        dialog.Title = "Export HMI screen";
                        if (dialog.ShowDialog(this) != DialogResult.OK)
                            return;

                        screen.Export(new FileInfo(dialog.FileName), ExportOptions.WithDefaults);
                        Log("Exported screen: " + screen.Name + " -> " + dialog.FileName);
                    }
                    return;
                }

                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Choose a folder for the selected screen folder export";
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                        return;

                    ExportFolderTag(_screenTree.SelectedNode.Tag, dialog.SelectedPath);
                    Log("Exported selected screen folder -> " + dialog.SelectedPath);
                }
            }
            catch (Exception ex)
            {
                ShowError("Screen export failed.", ex);
            }
        }

        private void ExportAllScreens()
        {
            if (_activeHmi == null)
                return;

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the root export folder";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                try
                {
                    string root = Path.Combine(dialog.SelectedPath, "Screens");
                    Directory.CreateDirectory(root);
                    ExportSystemFolder(_activeHmi.ScreenFolder, root);
                    Log("Exported all HMI screens -> " + root);
                }
                catch (Exception ex)
                {
                    ShowError("Export all screens failed.", ex);
                }
            }
        }

        private void ExportFolderTag(object tag, string outputPath)
        {
            var systemFolder = tag as ScreenSystemFolder;
            if (systemFolder != null)
            {
                ExportSystemFolder(systemFolder, outputPath);
                return;
            }

            var userFolder = tag as ScreenUserFolder;
            if (userFolder != null)
            {
                ExportUserFolder(userFolder, outputPath);
                return;
            }

            throw new InvalidOperationException("Select a screen or a screen folder.");
        }

        private void ExportSystemFolder(ScreenSystemFolder folder, string outputPath)
        {
            Directory.CreateDirectory(outputPath);
            ExportComposition(folder.Screens, outputPath);
            foreach (ScreenUserFolder subFolder in folder.Folders)
                ExportUserFolder(subFolder, Path.Combine(outputPath, SafeName(subFolder.Name)));
        }

        private void ExportUserFolder(ScreenUserFolder folder, string outputPath)
        {
            Directory.CreateDirectory(outputPath);
            ExportComposition(folder.Screens, outputPath);
            foreach (ScreenUserFolder subFolder in folder.Folders)
                ExportUserFolder(subFolder, Path.Combine(outputPath, SafeName(subFolder.Name)));
        }

        private void ExportComposition(ScreenComposition screens, string outputPath)
        {
            foreach (HmiScreen screen in screens)
            {
                string file = Path.Combine(outputPath, SafeName(screen.Name) + ".xml");
                screen.Export(new FileInfo(file), ExportOptions.WithDefaults);
                Log("  exported " + screen.Name);
            }
        }

        private void ExportCompleteHmi()
        {
            if (_activeHmi == null)
                return;

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the parent folder for the complete HMI export";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string rootName = "HMI Export - " + SafeName(GetActiveHmiName()) + " - " + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string root = Path.Combine(dialog.SelectedPath, rootName);
                Directory.CreateDirectory(root);

                var summary = new List<string>();
                summary.Add("TIA Portal " + TiaPortalAssemblyResolver.SelectedEngineeringVersion + " HMI Openness export");
                summary.Add("Exported: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                summary.Add("Project: " + (_project == null ? "" : _projectLabel.Text));
                summary.Add("HMI: " + GetActiveHmiName());
                summary.Add("");

                int ok = 0;
                int failed = 0;

                Log("Starting complete HMI export -> " + root);

                RunExportCategory("ProjectGraphics", summary, ref ok, ref failed, delegate
                {
                    string path = Path.Combine(root, "ProjectGraphics");
                    Directory.CreateDirectory(path);
                    ExportProjectGraphics(path);
                });

                RunExportCategory("Screens", summary, ref ok, ref failed, delegate
                {
                    string path = Path.Combine(root, "Screens");
                    Directory.CreateDirectory(path);
                    ExportSystemFolder(_activeHmi.ScreenFolder, path);
                });

                RunExportCategory("TagTables", summary, ref ok, ref failed, delegate
                {
                    string path = Path.Combine(root, "TagTables");
                    Directory.CreateDirectory(path);
                    ExportTagFolderDynamic((dynamic)_activeHmi.TagFolder, path);
                });

                RunExportCategory("VBScripts", summary, ref ok, ref failed, delegate
                {
                    string path = Path.Combine(root, "VBScripts");
                    Directory.CreateDirectory(path);
                    ExportVBScriptFolderDynamic((dynamic)_activeHmi.VBScriptFolder, path);
                });

                RunExportCategory("TextLists", summary, ref ok, ref failed, delegate
                {
                    string path = Path.Combine(root, "TextLists");
                    Directory.CreateDirectory(path);
                    ExportNamedCompositionDynamic((dynamic)_activeHmi.TextLists, path, ExportOptions.WithDefaults, "text list");
                });

                RunExportCategory("GraphicLists", summary, ref ok, ref failed, delegate
                {
                    string path = Path.Combine(root, "GraphicLists");
                    Directory.CreateDirectory(path);
                    ExportNamedCompositionDynamic((dynamic)_activeHmi.GraphicLists, path, ExportOptions.WithDefaults, "graphic list");
                });

                RunExportCategory("Connections", summary, ref ok, ref failed, delegate
                {
                    string path = Path.Combine(root, "Connections");
                    Directory.CreateDirectory(path);
                    ExportNamedCompositionDynamic((dynamic)_activeHmi.Connections, path, ExportOptions.WithDefaults, "connection");
                });

                RunExportCategory("Cycles", summary, ref ok, ref failed, delegate
                {
                    string path = Path.Combine(root, "Cycles");
                    Directory.CreateDirectory(path);
                    ExportNamedCompositionDynamic((dynamic)_activeHmi.Cycles, path, ExportOptions.WithDefaults, "cycle");
                });

                // The following screen types are device-dependent.  Each category is isolated
                // so an unsupported feature does not stop the rest of the complete export.
                RunExportCategory("PermanentArea", summary, ref ok, ref failed, delegate
                {
                    dynamic overview = ((dynamic)_activeHmi).ScreenOverview;
                    if (overview == null)
                    {
                        Log("  no permanent area available");
                        return;
                    }
                    string path = Path.Combine(root, "PermanentArea");
                    Directory.CreateDirectory(path);
                    string file = Path.Combine(path, "PermanentArea.xml");
                    overview.Export(new FileInfo(file), ExportOptions.WithDefaults);
                    Log("  exported permanent area");
                });

                RunExportCategory("ScreenTemplates", summary, ref ok, ref failed, delegate
                {
                    string path = Path.Combine(root, "ScreenTemplates");
                    Directory.CreateDirectory(path);
                    dynamic folder = ((dynamic)_activeHmi).ScreenTemplateFolder;
                    ExportTemplateFolderDynamic(folder, path);
                });

                RunExportCategory("PopupScreens", summary, ref ok, ref failed, delegate
                {
                    string path = Path.Combine(root, "PopupScreens");
                    Directory.CreateDirectory(path);
                    dynamic folder = ((dynamic)_activeHmi).ScreenPopupFolder;
                    ExportPopupFolderDynamic(folder, path);
                });

                RunExportCategory("SlideInScreens", summary, ref ok, ref failed, delegate
                {
                    string path = Path.Combine(root, "SlideInScreens");
                    Directory.CreateDirectory(path);
                    dynamic folder = ((dynamic)_activeHmi).ScreenSlideinFolder;
                    int count = 0;
                    foreach (dynamic slidein in folder.ScreenSlideins)
                    {
                        string typeName = Convert.ToString(slidein.SlideinType);
                        string file = Path.Combine(path, SafeName(typeName) + ".xml");
                        slidein.Export(new FileInfo(file), ExportOptions.WithDefaults);
                        count++;
                        Log("  exported slide-in: " + typeName);
                    }
                    if (count == 0)
                        Log("  no slide-in screens found");
                });

                summary.Add("");
                summary.Add("Categories completed: " + ok);
                summary.Add("Categories failed/skipped by API exception: " + failed);
                summary.Add("");
                summary.Add("Notes:");
                summary.Add("- Each category is exported independently. Unsupported device features do not stop the export.");
                summary.Add("- Siemens does not support exporting integrated HMI connections; such connections may be logged as failures.");
                summary.Add("- VB scripts are exported with ExportOptions.None, matching Siemens' classic-HMI example.");
                summary.Add("- ProjectGraphics contains each project graphic in its own folder so its sidecar image files stay with the XML.");
                summary.Add("- Screen objects and HMI references may contain Open Links to objects outside the exported XML file.");

                File.WriteAllLines(Path.Combine(root, "ExportSummary.txt"), summary.ToArray());
                Log("Complete HMI export finished. Categories OK: " + ok + ", failed/skipped: " + failed);

                MessageBox.Show(this,
                    "Complete HMI export finished." + Environment.NewLine + Environment.NewLine +
                    "Folder: " + root + Environment.NewLine +
                    "Categories completed: " + ok + Environment.NewLine +
                    "Categories failed/skipped: " + failed + Environment.NewLine + Environment.NewLine +
                    "See ExportSummary.txt and the log for details.",
                    failed == 0 ? "Export complete" : "Export complete with notes",
                    MessageBoxButtons.OK,
                    failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
        }

        private void ExportProjectGraphics(string outputPath)
        {
            if (_project == null)
                throw new InvalidOperationException("No TIA project is attached.");

            int count = 0;
            dynamic graphics = NativeProjectResources.Graphics(_project);
            foreach (dynamic graphic in graphics)
            {
                string name = Convert.ToString(graphic.Name);
                string graphicFolder = Path.Combine(outputPath, SafeName(name));
                Directory.CreateDirectory(graphicFolder);
                string file = Path.Combine(graphicFolder, SafeName(name) + ".xml");
                graphic.Export(new FileInfo(file), ExportOptions.WithDefaults);
                count++;
                Log("  exported project graphic: " + name);
            }
            if (count == 0)
                Log("  no project graphics found");
        }

        private void RunExportCategory(string name, List<string> summary, ref int ok, ref int failed, Action action)
        {
            try
            {
                Log("Exporting " + name + "...");
                action();
                ok++;
                summary.Add("OK     " + name);
            }
            catch (Exception ex)
            {
                failed++;
                string detail = GetInnermostMessage(ex);
                summary.Add("FAILED " + name + " - " + detail);
                Log("  " + name + " FAILED/SKIPPED: " + detail);
            }
        }

        private void ExportTagFolderDynamic(dynamic folder, string outputPath)
        {
            Directory.CreateDirectory(outputPath);

            foreach (dynamic table in folder.TagTables)
            {
                string name = Convert.ToString(table.Name);
                string file = Path.Combine(outputPath, SafeName(name) + ".xml");
                try
                {
                    table.Export(new FileInfo(file), ExportOptions.WithDefaults);
                    Log("  exported tag table: " + name);
                }
                catch (Exception ex)
                {
                    Log("  tag table FAILED: " + name + " - " + GetInnermostMessage(ex));
                }
            }

            foreach (dynamic subFolder in folder.Folders)
            {
                string name = Convert.ToString(subFolder.Name);
                ExportTagFolderDynamic(subFolder, Path.Combine(outputPath, SafeName(name)));
            }
        }

        private void ExportVBScriptFolderDynamic(dynamic folder, string outputPath)
        {
            Directory.CreateDirectory(outputPath);

            dynamic scripts = folder.VBScripts;
            if (scripts != null)
            {
                foreach (dynamic script in scripts)
                {
                    string name = Convert.ToString(script.Name);
                    string file = Path.Combine(outputPath, SafeName(name) + ".xml");
                    try
                    {
                        script.Export(new FileInfo(file), ExportOptions.None);
                        Log("  exported VB script: " + name);
                    }
                    catch (Exception ex)
                    {
                        Log("  VB script FAILED: " + name + " - " + GetInnermostMessage(ex));
                    }
                }
            }

            foreach (dynamic subFolder in folder.Folders)
            {
                string name = Convert.ToString(subFolder.Name);
                ExportVBScriptFolderDynamic(subFolder, Path.Combine(outputPath, SafeName(name)));
            }
        }

        private void ExportNamedCompositionDynamic(dynamic composition, string outputPath, ExportOptions options, string itemLabel)
        {
            int count = 0;
            foreach (dynamic item in composition)
            {
                string name = Convert.ToString(item.Name);
                string file = Path.Combine(outputPath, SafeName(name) + ".xml");
                try
                {
                    item.Export(new FileInfo(file), options);
                    count++;
                    Log("  exported " + itemLabel + ": " + name);
                }
                catch (Exception ex)
                {
                    Log("  " + itemLabel + " FAILED: " + name + " - " + GetInnermostMessage(ex));
                }
            }
            if (count == 0)
                Log("  no exportable " + itemLabel + " objects found");
        }

        private void ExportTemplateFolderDynamic(dynamic folder, string outputPath)
        {
            Directory.CreateDirectory(outputPath);
            foreach (dynamic template in folder.ScreenTemplates)
            {
                string name = Convert.ToString(template.Name);
                string file = Path.Combine(outputPath, SafeName(name) + ".xml");
                template.Export(new FileInfo(file), ExportOptions.WithDefaults);
                Log("  exported screen template: " + name);
            }
            foreach (dynamic subFolder in folder.Folders)
            {
                string name = Convert.ToString(subFolder.Name);
                ExportTemplateFolderDynamic(subFolder, Path.Combine(outputPath, SafeName(name)));
            }
        }

        private void ExportPopupFolderDynamic(dynamic folder, string outputPath)
        {
            Directory.CreateDirectory(outputPath);
            foreach (dynamic popup in folder.ScreenPopups)
            {
                string name = Convert.ToString(popup.Name);
                string file = Path.Combine(outputPath, SafeName(name) + ".xml");
                popup.Export(new FileInfo(file), ExportOptions.WithDefaults);
                Log("  exported pop-up: " + name);
            }
            foreach (dynamic subFolder in folder.Folders)
            {
                string name = Convert.ToString(subFolder.Name);
                ExportPopupFolderDynamic(subFolder, Path.Combine(outputPath, SafeName(name)));
            }
        }

        private string GetActiveHmiName()
        {
            TargetEntry entry = _hmiCombo.SelectedItem as TargetEntry;
            if (entry != null)
                return entry.Reference.Label;
            return "HMI";
        }

        private static string GetInnermostMessage(Exception ex)
        {
            Exception current = ex;
            while (current.InnerException != null)
                current = current.InnerException;
            return current.Message;
        }

        private void ImportCompleteHmi()
        {
            if (_activeHmi == null || _project == null)
                return;

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the root folder created by Export Complete HMI";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string root = ResolveCompleteExportRoot(dialog.SelectedPath);
                if (root == null)
                {
                    MessageBox.Show(this,
                        "That folder does not look like a Complete HMI export. Select the folder that contains ExportSummary.txt and the category folders.",
                        "Invalid export folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                ImportOptions options = _overwriteCheck.Checked ? ImportOptions.Override : ImportOptions.None;
                string modeText = _overwriteCheck.Checked ? "OVERWRITE existing objects" : "create only / do not overwrite existing objects";

                DialogResult confirm = MessageBox.Show(this,
                    "Import this Complete HMI export into:" + Environment.NewLine + Environment.NewLine +
                    GetActiveHmiName() + Environment.NewLine + Environment.NewLine +
                    "Mode: " + modeText + Environment.NewLine + Environment.NewLine +
                    "The project will NOT be saved automatically. Cycles that already exist are always skipped for safety." +
                    Environment.NewLine + Environment.NewLine + "Continue?",
                    "Confirm Complete HMI import", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes)
                    return;

                var summary = new List<string>();
                summary.Add("TIA Portal " + TiaPortalAssemblyResolver.SelectedEngineeringVersion + " Complete HMI import");
                summary.Add("Imported: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                summary.Add("Project: " + _projectLabel.Text);
                summary.Add("HMI: " + GetActiveHmiName());
                summary.Add("Source: " + root);
                summary.Add("Mode: " + modeText);
                summary.Add("");

                int ok = 0;
                int failed = 0;
                Log("Starting Complete HMI import <- " + root);

                // Dependency-aware order for the exported classic HMI objects.
                // Project graphics are required by graphic lists and many screen objects.
                RunImportCategoryIfPresent("ProjectGraphics", root, summary, ref ok, ref failed, delegate(string path)
                {
                    ImportXmlFilesRecursiveDynamic(NativeProjectResources.Graphics(_project), path, options, "project graphic");
                });

                // External tags link to the HMI connection.
                RunImportCategoryIfPresent("Connections", root, summary, ref ok, ref failed, delegate(string path)
                {
                    ImportXmlFilesDynamic((dynamic)_activeHmi.Connections, path, options, "connection");
                });

                // Never override existing cycles: Siemens warns that attempts to change attributes
                // of standard cycles can cause a NonRecoverableException. Import missing cycles only.
                RunImportCategoryIfPresent("Cycles", root, summary, ref ok, ref failed, delegate(string path)
                {
                    ImportMissingCycles(path);
                });

                // Tag event handlers may call exported VB scripts, so scripts come first.
                RunImportCategoryIfPresent("VBScripts", root, summary, ref ok, ref failed, delegate(string path)
                {
                    ImportVBScriptFolderDynamic((dynamic)_activeHmi.VBScriptFolder, path, options);
                });

                // Tags can depend on connections, cycles, and scripts. Text lists can also contain
                // Open Links to HMI tags, so tags must precede text lists.
                RunImportCategoryIfPresent("TagTables", root, summary, ref ok, ref failed, delegate(string path)
                {
                    ImportTagFolderDynamic((dynamic)_activeHmi.TagFolder, path, options);
                });

                RunImportCategoryIfPresent("TextLists", root, summary, ref ok, ref failed, delegate(string path)
                {
                    ImportXmlFilesDynamic((dynamic)_activeHmi.TextLists, path, options, "text list");
                });

                RunImportCategoryIfPresent("GraphicLists", root, summary, ref ok, ref failed, delegate(string path)
                {
                    ImportXmlFilesDynamic((dynamic)_activeHmi.GraphicLists, path, options, "graphic list");
                });

                // Shared screen resources before the normal screens.
                RunImportCategoryIfPresent("ScreenTemplates", root, summary, ref ok, ref failed, delegate(string path)
                {
                    ImportScreenTemplateFolderDynamic((dynamic)_activeHmi.ScreenTemplateFolder, path, options);
                });

                RunImportCategoryIfPresent("PopupScreens", root, summary, ref ok, ref failed, delegate(string path)
                {
                    ImportPopupFolderDynamic((dynamic)_activeHmi.ScreenPopupFolder, path, options);
                });

                RunImportCategoryIfPresent("SlideInScreens", root, summary, ref ok, ref failed, delegate(string path)
                {
                    ImportXmlFilesDynamic((dynamic)_activeHmi.ScreenSlideinFolder.ScreenSlideins, path, options, "slide-in screen");
                });

                RunImportCategoryIfPresent("PermanentArea", root, summary, ref ok, ref failed, delegate(string path)
                {
                    string file = Directory.GetFiles(path, "*.xml", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (file == null)
                    {
                        Log("  no permanent area XML found");
                        return;
                    }
                    ((dynamic)_activeHmi).ImportScreenOverview(new FileInfo(file), options);
                    Log("  imported permanent area");
                });

                // Screens are last because they reference tags, text/graphic lists, project graphics,
                // VB scripts, the template, popups and other screens.
                RunImportCategoryIfPresent("Screens", root, summary, ref ok, ref failed, delegate(string path)
                {
                    ImportScreenFolderDynamic((dynamic)_activeHmi.ScreenFolder, path, options);
                });

                summary.Add("");
                summary.Add("Categories completed: " + ok);
                summary.Add("Categories failed: " + failed);
                summary.Add("Project saved automatically: NO");
                summary.Add("Cycles: existing cycles were skipped; only missing cycles were imported with ImportOptions.None.");

                string report = Path.Combine(root, "ImportSummary-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllLines(report, summary.ToArray());

                LoadScreenTree();
                Log("Complete HMI import finished. Categories OK: " + ok + ", failed: " + failed);

                MessageBox.Show(this,
                    "Complete HMI import finished." + Environment.NewLine + Environment.NewLine +
                    "Categories completed: " + ok + Environment.NewLine +
                    "Categories failed: " + failed + Environment.NewLine + Environment.NewLine +
                    "The TIA project has NOT been saved. Inspect/compile it in TIA, then use Save project when satisfied." +
                    Environment.NewLine + Environment.NewLine + "Report: " + report,
                    failed == 0 ? "Import complete" : "Import complete with errors",
                    MessageBoxButtons.OK,
                    failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
        }

        private string ResolveCompleteExportRoot(string selectedPath)
        {
            if (File.Exists(Path.Combine(selectedPath, "ExportSummary.txt")))
                return selectedPath;

            try
            {
                string[] children = Directory.GetDirectories(selectedPath);
                string[] matches = children.Where(d => File.Exists(Path.Combine(d, "ExportSummary.txt"))).ToArray();
                if (matches.Length == 1)
                    return matches[0];
            }
            catch { }
            return null;
        }

        private void RunImportCategoryIfPresent(string name, string root, List<string> summary, ref int ok, ref int failed, Action<string> action)
        {
            string path = Path.Combine(root, name);
            if (!Directory.Exists(path))
            {
                summary.Add("SKIP   " + name + " - category folder not present");
                Log("Skipping " + name + ": folder not present");
                return;
            }

            try
            {
                Log("Importing " + name + "...");
                action(path);
                ok++;
                summary.Add("OK     " + name);
            }
            catch (Exception ex)
            {
                failed++;
                string detail = GetInnermostMessage(ex);
                summary.Add("FAILED " + name + " - " + detail);
                Log("  " + name + " FAILED: " + detail);
            }
        }

        private void ImportXmlFilesDynamic(dynamic composition, string sourcePath, ImportOptions options, string label)
        {
            int count = 0;
            foreach (string file in Directory.GetFiles(sourcePath, "*.xml", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    composition.Import(new FileInfo(file), options);
                    count++;
                    Log("  imported " + label + ": " + Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    Log("  " + label + " FAILED: " + Path.GetFileName(file) + " - " + GetInnermostMessage(ex));
                    throw;
                }
            }
            if (count == 0)
                Log("  no " + label + " XML files found");
        }

        private void ImportXmlFilesRecursiveDynamic(dynamic composition, string sourcePath, ImportOptions options, string label)
        {
            int count = 0;
            foreach (string file in Directory.GetFiles(sourcePath, "*.xml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                composition.Import(new FileInfo(file), options);
                count++;
                Log("  imported " + label + ": " + Path.GetFileName(file));
            }
            if (count == 0)
                Log("  no " + label + " XML files found");
        }

        private void ImportMissingCycles(string sourcePath)
        {
            dynamic cycles = (dynamic)_activeHmi.Cycles;
            int imported = 0;
            int skipped = 0;
            foreach (string file in Directory.GetFiles(sourcePath, "*.xml", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                dynamic existing = cycles.Find(name);
                if (existing != null)
                {
                    skipped++;
                    Log("  skipped existing cycle: " + name);
                    continue;
                }

                cycles.Import(new FileInfo(file), ImportOptions.None);
                imported++;
                Log("  imported missing cycle: " + name);
            }
            Log("  cycles imported: " + imported + ", existing skipped: " + skipped);
        }

        private void ImportTagFolderDynamic(dynamic targetFolder, string sourcePath, ImportOptions options)
        {
            foreach (string file in Directory.GetFiles(sourcePath, "*.xml", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                targetFolder.TagTables.Import(new FileInfo(file), options);
                Log("  imported tag table: " + Path.GetFileName(file));
            }

            foreach (string directory in Directory.GetDirectories(sourcePath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(directory);
                dynamic child = targetFolder.Folders.Find(name);
                if (child == null)
                {
                    child = targetFolder.Folders.Create(name);
                    Log("  created tag folder: " + name);
                }
                ImportTagFolderDynamic(child, directory, options);
            }
        }

        private void ImportVBScriptFolderDynamic(dynamic targetFolder, string sourcePath, ImportOptions options)
        {
            foreach (string file in Directory.GetFiles(sourcePath, "*.xml", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                targetFolder.VBScripts.Import(new FileInfo(file), options);
                Log("  imported VB script: " + Path.GetFileName(file));
            }

            foreach (string directory in Directory.GetDirectories(sourcePath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(directory);
                dynamic child = targetFolder.Folders.Find(name);
                if (child == null)
                {
                    child = targetFolder.Folders.Create(name);
                    Log("  created VB script folder: " + name);
                }
                ImportVBScriptFolderDynamic(child, directory, options);
            }
        }

        private void ImportScreenTemplateFolderDynamic(dynamic targetFolder, string sourcePath, ImportOptions options)
        {
            foreach (string file in Directory.GetFiles(sourcePath, "*.xml", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                targetFolder.ScreenTemplates.Import(new FileInfo(file), options);
                Log("  imported screen template: " + Path.GetFileName(file));
            }

            foreach (string directory in Directory.GetDirectories(sourcePath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(directory);
                dynamic child = targetFolder.Folders.Find(name);
                if (child == null)
                {
                    child = targetFolder.Folders.Create(name);
                    Log("  created screen template folder: " + name);
                }
                ImportScreenTemplateFolderDynamic(child, directory, options);
            }
        }

        private void ImportPopupFolderDynamic(dynamic targetFolder, string sourcePath, ImportOptions options)
        {
            foreach (string file in Directory.GetFiles(sourcePath, "*.xml", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                targetFolder.ScreenPopups.Import(new FileInfo(file), options);
                Log("  imported pop-up screen: " + Path.GetFileName(file));
            }

            foreach (string directory in Directory.GetDirectories(sourcePath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(directory);
                dynamic child = targetFolder.Folders.Find(name);
                if (child == null)
                {
                    child = targetFolder.Folders.Create(name);
                    Log("  created pop-up folder: " + name);
                }
                ImportPopupFolderDynamic(child, directory, options);
            }
        }

        private void ImportScreenFolderDynamic(dynamic targetFolder, string sourcePath, ImportOptions options)
        {
            foreach (string file in Directory.GetFiles(sourcePath, "*.xml", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                targetFolder.Screens.Import(new FileInfo(file), options);
                Log("  imported screen: " + Path.GetFileName(file));
            }

            foreach (string directory in Directory.GetDirectories(sourcePath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(directory);
                dynamic child = targetFolder.Folders.Find(name);
                if (child == null)
                {
                    child = targetFolder.Folders.Create(name);
                    Log("  created screen folder: " + name);
                }
                ImportScreenFolderDynamic(child, directory, options);
            }
        }

        private void ImportScreensOnly()
        {
            if (_activeHmi == null || _project == null)
                return;

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the Complete HMI export root or its Screens folder";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string reportRoot;
                string screensRoot = ResolveScreensSourceFolder(dialog.SelectedPath, out reportRoot);
                if (screensRoot == null)
                {
                    MessageBox.Show(this,
                        "No Screens folder was found. Select either the Complete HMI export root or the Screens folder inside it.",
                        "Screens folder not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string[] screenFiles = Directory.GetFiles(screensRoot, "*.xml", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                if (screenFiles.Length == 0)
                {
                    MessageBox.Show(this, "No screen XML files were found under:" + Environment.NewLine + screensRoot,
                        "No screens found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                ImportOptions options = _overwriteCheck.Checked ? ImportOptions.Override : ImportOptions.None;
                string modeText = _overwriteCheck.Checked ? "OVERWRITE existing screens" : "create only / do not overwrite existing screens";

                DialogResult confirm = MessageBox.Show(this,
                    "Import ONLY normal HMI screens into:" + Environment.NewLine + Environment.NewLine +
                    GetActiveHmiName() + Environment.NewLine + Environment.NewLine +
                    "Source: " + screensRoot + Environment.NewLine +
                    "Screens found: " + screenFiles.Length + Environment.NewLine +
                    "Mode: " + modeText + Environment.NewLine + Environment.NewLine +
                    "Tag tables, text lists, scripts, graphics, templates, pop-ups, slide-ins, connections, cycles and the permanent area will NOT be imported." +
                    Environment.NewLine + Environment.NewLine +
                    "Each screen is imported separately. A failed or cancelled screen is logged and the utility continues with the remaining screens." +
                    Environment.NewLine + Environment.NewLine +
                    "The project will NOT be saved automatically. Continue?",
                    "Confirm Screens-Only import", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes)
                    return;

                var summary = new List<string>();
                summary.Add("TIA Portal " + TiaPortalAssemblyResolver.SelectedEngineeringVersion + " Screens-Only HMI import");
                summary.Add("Imported: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                summary.Add("Project: " + _projectLabel.Text);
                summary.Add("HMI: " + GetActiveHmiName());
                summary.Add("Source: " + screensRoot);
                summary.Add("Mode: " + modeText);
                summary.Add("Screen files found: " + screenFiles.Length);
                summary.Add("");

                int imported = 0;
                int failed = 0;
                int index = 0;

                Log("Starting Screens-Only import <- " + screensRoot);
                SetImportBusy(true);
                try
                {
                    ImportScreenFolderResilientDynamic((dynamic)_activeHmi.ScreenFolder, screensRoot, options,
                        summary, ref imported, ref failed, ref index, screenFiles.Length, "");
                }
                finally
                {
                    SetImportBusy(false);
                }

                summary.Add("");
                summary.Add("Screens imported: " + imported);
                summary.Add("Screens failed/cancelled: " + failed);
                summary.Add("Total screen files: " + screenFiles.Length);
                summary.Add("Project saved automatically: NO");

                string report = Path.Combine(reportRoot, "ScreenImportSummary-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllLines(report, summary.ToArray());

                LoadScreenTree();
                Log("Screens-Only import finished. Imported: " + imported + ", failed/cancelled: " + failed + ".");

                MessageBox.Show(this,
                    "Screens-Only import finished." + Environment.NewLine + Environment.NewLine +
                    "Imported: " + imported + Environment.NewLine +
                    "Failed/cancelled: " + failed + Environment.NewLine +
                    "Total: " + screenFiles.Length + Environment.NewLine + Environment.NewLine +
                    "The TIA project has NOT been saved." + Environment.NewLine + Environment.NewLine +
                    "Report: " + report,
                    failed == 0 ? "Screens import complete" : "Screens import complete with errors",
                    MessageBoxButtons.OK,
                    failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
        }

        private string ResolveScreensSourceFolder(string selectedPath, out string reportRoot)
        {
            reportRoot = selectedPath;

            if (string.Equals(Path.GetFileName(selectedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                "Screens", StringComparison.OrdinalIgnoreCase))
            {
                reportRoot = Directory.GetParent(selectedPath) == null ? selectedPath : Directory.GetParent(selectedPath).FullName;
                return selectedPath;
            }

            string direct = Path.Combine(selectedPath, "Screens");
            if (Directory.Exists(direct))
            {
                reportRoot = selectedPath;
                return direct;
            }

            string completeRoot = ResolveCompleteExportRoot(selectedPath);
            if (completeRoot != null)
            {
                string nested = Path.Combine(completeRoot, "Screens");
                if (Directory.Exists(nested))
                {
                    reportRoot = completeRoot;
                    return nested;
                }
            }

            return null;
        }

        private void ImportScreenFolderResilientDynamic(dynamic targetFolder, string sourcePath, ImportOptions options,
            List<string> summary, ref int imported, ref int failed, ref int index, int total, string relativeFolder)
        {
            foreach (string file in Directory.GetFiles(sourcePath, "*.xml", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                index++;
                string displayPath = string.IsNullOrEmpty(relativeFolder)
                    ? Path.GetFileName(file)
                    : relativeFolder + "\\" + Path.GetFileName(file);

                try
                {
                    Log("  [" + index + "/" + total + "] importing screen: " + displayPath);
                    targetFolder.Screens.Import(new FileInfo(file), options);
                    imported++;
                    summary.Add("OK     [" + index + "/" + total + "] " + displayPath);
                    Log("  [" + index + "/" + total + "] OK: " + displayPath);
                }
                catch (Exception ex)
                {
                    failed++;
                    string detail = GetInnermostMessage(ex);
                    summary.Add("FAILED [" + index + "/" + total + "] " + displayPath + " - " + detail);
                    Log("  [" + index + "/" + total + "] FAILED: " + displayPath + " - " + detail);
                }

                _log.Refresh();
                Application.DoEvents();
            }

            foreach (string directory in Directory.GetDirectories(sourcePath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(directory);
                string childRelative = string.IsNullOrEmpty(relativeFolder) ? name : relativeFolder + "\\" + name;
                try
                {
                    dynamic child = targetFolder.Folders.Find(name);
                    if (child == null)
                    {
                        child = targetFolder.Folders.Create(name);
                        Log("  created screen folder: " + childRelative);
                    }

                    ImportScreenFolderResilientDynamic(child, directory, options, summary,
                        ref imported, ref failed, ref index, total, childRelative);
                }
                catch (Exception ex)
                {
                    string detail = GetInnermostMessage(ex);
                    int affected = Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories).Length;
                    failed += affected;
                    index += affected;
                    summary.Add("FAILED FOLDER " + childRelative + " - " + detail + " (" + affected + " screen file(s) skipped)");
                    Log("  screen folder FAILED: " + childRelative + " - " + detail + "; skipped " + affected + " screen file(s)");
                }
            }
        }

        private void SetImportBusy(bool busy)
        {
            UseWaitCursor = busy;
            _refreshProcessesButton.Enabled = !busy;
            _attachButton.Enabled = !busy;
            _hmiCombo.Enabled = !busy;
            _refreshScreensButton.Enabled = !busy && _activeHmi != null;
            _exportSelectedButton.Enabled = !busy && _activeHmi != null && _screenTree.SelectedNode != null;
            _exportAllButton.Enabled = !busy && _activeHmi != null;
            _exportCompleteButton.Enabled = !busy && _activeHmi != null;
            _importCompleteButton.Enabled = !busy && _activeHmi != null;
            _importScreensOnlyButton.Enabled = !busy && _activeHmi != null;
            _importButton.Enabled = !busy && _activeHmi != null;
            _overwriteCheck.Enabled = !busy && _activeHmi != null;
            _saveProjectButton.Enabled = !busy && _project != null;
        }

        private void ImportScreens()
        {
            if (_activeHmi == null)
                return;

            object targetFolder = GetSelectedImportFolder();
            if (targetFolder == null)
            {
                MessageBox.Show(this, "Select the Screens root, a screen folder, or a screen inside the destination folder.", "Select destination", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "TIA screen XML (*.xml)|*.xml|All files (*.*)|*.*";
                dialog.Multiselect = true;
                dialog.Title = "Import HMI screen XML";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                ImportOptions options = _overwriteCheck.Checked ? ImportOptions.Override : ImportOptions.None;
                int imported = 0;
                var failures = new List<string>();

                foreach (string file in dialog.FileNames)
                {
                    try
                    {
                        ImportIntoFolder(targetFolder, new FileInfo(file), options);
                        imported++;
                        Log("Imported: " + file);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(Path.GetFileName(file) + ": " + ex.Message);
                        Log("IMPORT FAILED: " + file + Environment.NewLine + "  " + ex.Message);
                    }
                }

                LoadScreenTree();

                string message = "Imported " + imported + " of " + dialog.FileNames.Length + " screen file(s).";
                if (failures.Count > 0)
                    message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(8));

                MessageBox.Show(this, message, failures.Count == 0 ? "Import complete" : "Import completed with errors",
                    MessageBoxButtons.OK, failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
        }

        private object GetSelectedImportFolder()
        {
            TreeNode node = _screenTree.SelectedNode;
            while (node != null)
            {
                if (node.Tag is ScreenSystemFolder || node.Tag is ScreenUserFolder)
                    return node.Tag;
                node = node.Parent;
            }
            return _activeHmi == null ? null : (object)_activeHmi.ScreenFolder;
        }

        private static void ImportIntoFolder(object folder, FileInfo file, ImportOptions options)
        {
            var systemFolder = folder as ScreenSystemFolder;
            if (systemFolder != null)
            {
                systemFolder.Screens.Import(file, options);
                return;
            }

            var userFolder = folder as ScreenUserFolder;
            if (userFolder != null)
            {
                userFolder.Screens.Import(file, options);
                return;
            }

            throw new InvalidOperationException("Unsupported destination screen folder.");
        }

        private void SaveProject()
        {
            if (_project == null)
                return;

            try
            {
                _project.Save();
                Log("Project saved." );
            }
            catch (Exception ex)
            {
                ShowError("Could not save the project.", ex);
            }
        }

        private void UpdateActionState()
        {
            bool connected = _project != null && _activeHmi != null;
            _refreshScreensButton.Enabled = _project != null && _activeTarget != null;
            bool classic = _activeTarget == null || _activeHmi != null;
            _engineeringPanel.Visible = !classic;
            _screenTree.Visible = classic;
            foreach (Control control in new Control[] { _exportSelectedButton, _exportAllButton, _exportCompleteButton, _importCompleteButton, _importScreensOnlyButton, _importButton, _overwriteCheck })
                control.Visible = classic;
            _actions.Height = classic ? 82 : 40;
            _exportAllButton.Enabled = connected;
            _exportCompleteButton.Enabled = connected;
            _importCompleteButton.Enabled = connected;
            _importScreensOnlyButton.Enabled = connected;
            _exportSelectedButton.Enabled = connected && _screenTree.SelectedNode != null;
            _importButton.Enabled = connected;
            _overwriteCheck.Enabled = connected;
            _saveProjectButton.Enabled = _project != null;
        }

        private static string SafeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private void Log(string message)
        {
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        }

        private void ShowError(string heading, Exception ex)
        {
            Log(heading + " " + ex.GetType().Name + ": " + ex.Message);
            MessageBox.Show(this, heading + Environment.NewLine + Environment.NewLine + ex.Message,
                "TIA Openness error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private sealed class ProcessEntry
        {
            public ProcessEntry(TiaPortalProcess process, string label)
            {
                Process = process;
                Label = label;
            }
            public TiaPortalProcess Process { get; private set; }
            public string Label { get; private set; }
            public override string ToString() { return Label; }
        }

        private sealed class TargetEntry
        {
            public TargetEntry(SoftwareReference reference) { Reference = reference; }
            public SoftwareReference Reference { get; private set; }
            public override string ToString() { return "[" + (Reference.Family == "classic" ? "Comfort / Advanced" : Reference.Family == "plc" ? "PLC" : "Unified") + "] " + Reference.Label; }
        }
    }
}
