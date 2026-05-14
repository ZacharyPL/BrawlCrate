using BrawlLib.SSBB.ResourceNodes;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BrawlCrate.NodeWrappers
{
    /// <summary>
    /// Embedded RWAR wave archive (Audio folder for RWSD v3+ / RBNK v2+ inside RSAR or standalone files).
    /// </summary>
    [NodeWrapper(ResourceType.RWAR)]
    public class RWARWrapper : GenericWrapper
    {
        #region Menu

        private static readonly ContextMenuStrip _menu;

        private static readonly ToolStripMenuItem DuplicateToolStripMenuItem =
            new ToolStripMenuItem("&Duplicate", null, DuplicateAction, Keys.Control | Keys.D);

        private static readonly ToolStripMenuItem ReplaceToolStripMenuItem =
            new ToolStripMenuItem("&Replace", null, ReplaceAction, Keys.Control | Keys.R);

        private static readonly ToolStripMenuItem RestoreToolStripMenuItem =
            new ToolStripMenuItem("Res&tore", null, RestoreAction, Keys.Control | Keys.T);

        private static readonly ToolStripMenuItem MoveUpToolStripMenuItem =
            new ToolStripMenuItem("Move &Up", null, MoveUpAction, Keys.Control | Keys.Up);

        private static readonly ToolStripMenuItem MoveDownToolStripMenuItem =
            new ToolStripMenuItem("Move D&own", null, MoveDownAction, Keys.Control | Keys.Down);

        private static readonly ToolStripMenuItem DeleteToolStripMenuItem =
            new ToolStripMenuItem("&Delete", null, DeleteAction, Keys.Control | Keys.Delete);

        static RWARWrapper()
        {
            _menu = new ContextMenuStrip();
            _menu.Items.Add(new ToolStripMenuItem("Import New Sound", null, CreateAction, Keys.Control | Keys.I));
            _menu.Items.Add(new ToolStripMenuItem("Mass Import WAVs…", null, MassImportFilesAction));
            _menu.Items.Add(new ToolStripMenuItem("Mass Import WAVs from Folder…", null, MassImportFolderAction));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("&Export", null, ExportAction, Keys.Control | Keys.E));
            _menu.Items.Add(DuplicateToolStripMenuItem);
            _menu.Items.Add(ReplaceToolStripMenuItem);
            _menu.Items.Add(new ToolStripMenuItem("Re&name", null, RenameAction, Keys.Control | Keys.N));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(MoveUpToolStripMenuItem);
            _menu.Items.Add(MoveDownToolStripMenuItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(DeleteToolStripMenuItem);
            _menu.Opening += MenuOpening;
            _menu.Closing += MenuClosing;
        }

        protected static void CreateAction(object sender, EventArgs e)
        {
            GetInstance<RWARWrapper>().ImportNewSound();
        }

        protected static void MassImportFilesAction(object sender, EventArgs e)
        {
            RWARWrapper w = GetInstance<RWARWrapper>();
            if (Program.OpenFiles(RsarMassWavImport.WavFilter, out string[] paths) > 0)
            {
                RsarMassWavImport.RunFromPaths(w._resource, paths);
            }
        }

        protected static void MassImportFolderAction(object sender, EventArgs e)
        {
            RWARWrapper w = GetInstance<RWARWrapper>();
            string folder = Program.ChooseFolder();
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            string[] wavs = Directory.GetFiles(folder, "*.wav", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
            RsarMassWavImport.RunFromPaths(w._resource, wavs);
        }

        private void ImportNewSound()
        {
            if (Program.OpenFile(RsarMassWavImport.WavFilter, out string path))
            {
                RWAVNode n = new RWAVNode();
                _resource.AddChild(n);
                n.Replace(path);

                BaseWrapper res = FindResource(n, true);
                res.EnsureVisible();
                res.TreeView.SelectedNode = res;
            }
        }

        private static void MenuClosing(object sender, ToolStripDropDownClosingEventArgs e)
        {
            DuplicateToolStripMenuItem.Enabled = true;
            ReplaceToolStripMenuItem.Enabled = true;
            RestoreToolStripMenuItem.Enabled = true;
            MoveUpToolStripMenuItem.Enabled = true;
            MoveDownToolStripMenuItem.Enabled = true;
            DeleteToolStripMenuItem.Enabled = true;
        }

        private static void MenuOpening(object sender, CancelEventArgs e)
        {
            RWARWrapper w = GetInstance<RWARWrapper>();

            DuplicateToolStripMenuItem.Enabled = w.Parent != null;
            ReplaceToolStripMenuItem.Enabled = w.Parent != null;
            RestoreToolStripMenuItem.Enabled = w._resource.IsDirty || w._resource.IsBranch;
            MoveUpToolStripMenuItem.Enabled = w.PrevNode != null;
            MoveDownToolStripMenuItem.Enabled = w.NextNode != null;
            DeleteToolStripMenuItem.Enabled = w.Parent != null;
        }

        #endregion

        public RWARWrapper()
        {
            ContextMenuStrip = _menu;
        }
    }
}
