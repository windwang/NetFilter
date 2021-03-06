﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace NetFilterApp
{
    public partial class MainForm : Form
    {
        #region Fields

        IntPtr netMon;
        bool netMonStarted;
        bool isInit;
        const string configPath = "settings.conf";

        Settings settings;
        Logger logger;
        TreeNode root;
        LogForm logForm;

        #endregion

        enum NetFilterStatus
        {
            NotStarted,
            Ready,
            Started,
            Stopped,
            Failed
        }

        #region TreeView handlers
        bool findParentNode(string expectedChild, ref TreeNode parent)
        {
            parent = null;
            TreeNode[] foundNodes = null;

            while (expectedChild != null)
            {
                foundNodes = root.Nodes.Find(expectedChild, true);
                if (foundNodes.Length > 0)
                {
                    parent = foundNodes[0];
                    return true;
                }
                expectedChild = Path.GetDirectoryName(expectedChild);
            }

            return false;
        }

        void GetFilesNodes(TreeNode node, ref List<TreeNode> filesNodesList)
        {
            foreach (TreeNode childNode in node.Nodes)
            {
                GetEmptyDirectoryNodes(childNode, ref filesNodesList);
                if ((string)childNode.Tag == "File")
                {
                    filesNodesList.Add(childNode);
                }
            }
        }

        void GetEmptyDirectoryNodes(TreeNode node, ref List<TreeNode> emptyNodesList)
        {
            foreach (TreeNode childNode in node.Nodes)
            {
                GetEmptyDirectoryNodes(childNode, ref emptyNodesList);
                if ((string)childNode.Tag == "Directory" && childNode.Nodes.Count == 0)
                {
                    emptyNodesList.Add(childNode);
                }
            }
        }

        void AddDirectory(string directoryPath)
        {
            try
            {
                TreeNode[] foundNodes = root.Nodes.Find(directoryPath, true);
        
                TreeNode parentNode = null, currentNode = root;
                if (foundNodes.Length == 0)
                {
                    string textNode = directoryPath;
                    if (findParentNode(directoryPath, ref currentNode))
                    {
                        textNode = Path.GetFileName(directoryPath);
                    }
                    else
                    {
                        currentNode = (foundNodes.Length > 0) ? foundNodes[0] : root;
                    }

                    parentNode = currentNode.Nodes.Add(directoryPath, textNode);

                    parentNode.Tag = "Directory";
                    parentNode.ContextMenuStrip = folderContextMenuStrip;

                    currentNode.Expand();
                }

                foreach (var subdir in
                    Directory.GetDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly))
                {
                    AddDirectory(subdir);
                }

                foreach (var process in
                    Directory.GetFiles(directoryPath, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    AddProcess(process, parentNode);
                }

                if (currentNode.Nodes.Count == 0)
                {
                    DeleteSelectedTreeItem(parentNode);
                }
            }
            catch (Exception e)
            {
                logger.write(e.Message);
            }
        }


        /*
         * Create different subdir(nodes) in treeView between parentDir and file
         * for example parentDir is C:\soft and file C:\Soft\myfolder\misc\file.txt
         * after calling function in parentDir creates myfolder node and in myfolder node
         * creates node misc and returns this node
        */
        TreeNode CreateDeltaSubDirs(TreeNode parentDir, string file)
        {
            TreeNode result = null;
            string fileDirectory = Path.GetDirectoryName(file);

            if (fileDirectory.Equals(parentDir.Name))
            {
                return parentDir;
            }

            bool startWith = fileDirectory.StartsWith(parentDir.Name);
            TreeNode parent = null;
            if (findParentNode(file, ref parent) && startWith)
            {
                string diff = fileDirectory.Replace(parentDir.Name, "");
                string collector = parentDir.Name;
                foreach (var subDir in diff.Split('\\'))
                {
                    if (subDir != string.Empty)
                    {
                        collector = Path.Combine(collector, subDir);
                        parentDir = parentDir.Nodes.Add(collector, subDir);
                    }
                }

                result = parentDir;
            }

            return result;
        }

        void AddProcess(string processPath, TreeNode parent=null, bool addToSettings=true)
        {
            TreeNode[] foundNodes = root.Nodes.Find(processPath, true);
            if (foundNodes.Length == 0)
            {
                string dirName = Path.GetDirectoryName(processPath);
                string fileName = Path.GetFileName(processPath);

                if (addToSettings)
                {
                    settings.addTracingProcess(processPath);
                }

                if (foundNodes.Length == 0)
                {
                    if (parent == null)
                    {
                        TreeNode foundParentNode = null;
                        if (findParentNode(dirName, ref foundParentNode))
                        {
                            TreeNode tmpNode = CreateDeltaSubDirs(foundParentNode, processPath);
                            if (tmpNode != null)
                            {
                                parent = tmpNode;
                            }
                            else
                            {
                                parent = root.Nodes.Add(dirName, dirName);
                            }
                        }
                        else
                        {
                            parent = root.Nodes.Add(dirName, dirName);
                        }
                        parent.Tag = "Directory";
                        parent.ContextMenuStrip = folderContextMenuStrip;
                    }

                    TreeNode fileNode = parent.Nodes.Add(processPath, fileName);

                    fileNode.Tag = "File";
                    fileNode.ContextMenuStrip = fileContextMenuStrip;

                    parent.Parent.Expand();
                    parent.Expand();
                }
            }
        }

        void DeleteSelectedTreeItem(TreeNode node)
        {
            TreeNode parentNode = node.Parent;

            if ((string)node.Tag == "File")
            {
                List<TreeNode> fileNodes = new List<TreeNode>();

                GetFilesNodes(node, ref fileNodes);
                foreach (TreeNode fileNode in fileNodes)
                {
                    settings.deleteTracingProcess(fileNode.Name);
                }
            }

            node.Remove();

            var topNode = root;

            if (parentNode != null && parentNode != root
                && parentNode.Nodes.Count == 0)
            {
                parentNode.Remove();
            }

            if (root.Nodes.Count == 0)
            {
                updateFormItems(NetFilterStatus.NotStarted);
            }
        }

        #endregion

        #region Netfilter methods
        void StartNetFilter()
        {
            if (RefreshSettings() && NetFilterWrap.NetMonStart(netMon))
            {
                updateFormItems(NetFilterStatus.Started);
                logger.write("Netfilter started..");
            }
            else
            {
                updateFormItems(NetFilterStatus.Failed);
                logger.write("Error during starting netfilter..");
            }
        }

        void StopNetfilter()
        {
            if (NetFilterWrap.NetMonIsStarted(netMon))
            {
                NetFilterWrap.NetMonStop(netMon);
                updateFormItems(NetFilterStatus.Stopped);

                logger.write("Netfilter has been stopped..");
            }
            else
            {
                logger.write("Netfilter is not started..");
            }
        }

        bool RefreshSettings()
        {
            logger.write("Refreshing settings..");

            bool settingsWrited = settings.write();
            if (!settingsWrited)
            {
                logger.write("Couldn't write settings");
            }
            else
            {
                bool netMonStarted = NetFilterWrap.NetMonIsStarted(netMon);
                if (NetFilterWrap.NetMonIsStarted(netMon))
                {
                    NetFilterWrap.NetMonRefreshSetting(netMon);
                    logger.write("Settings has been refreshed..");
                }
                else
                {
                    logger.write("Netfilter is not started..");
                }
            }

            return settingsWrited;
        }
        #endregion

        void InitializeFields()
        {
            logger = new Logger();

            netMon = IntPtr.Zero;
            netMonStarted = false;
            isInit = false;
            settings = new Settings(configPath, logger);

            root = filteredAppsTreeView.Nodes.Add("root", Environment.MachineName);

            logForm = new LogForm();
        }

        #region MainForm handlers

        public MainForm()
        {
            InitializeComponent();
            InitializeFields();

            try
            {
                netMon = NetFilterWrap.NetMonCreate();
                isInit = (netMon != IntPtr.Zero);
            }
            catch (Exception e)
            {
                logger.write(string.Format("{0} {1}", e.GetType(), e.Message));
            }
        }

        ~MainForm()
        {
            if (netMonStarted)
            {
                try
                {
                    NetFilterWrap.NetMonStop(netMon);
                }
                catch (Exception e)
                {
                    logger.write(string.Format("{0} {1}", e.GetType(), e.Message));
                }
            }

            if (netMon != null)
            {
                try
                {
                    NetFilterWrap.NetMonFree(netMon);
                }
                catch (Exception e)
                {
                    logger.write(string.Format("{0} {1}", e.GetType(), e.Message));
                }
            }

            netMon = IntPtr.Zero;
        }

        void updateFormItems(NetFilterStatus status)
        {
            if (status == NetFilterStatus.Started)
            {
                stopFilterButton.Enabled = true;
                refreshSettingsButton.Enabled = true;

                addFolderButton.Enabled = false;
                addAppButton.Enabled = false;
                startFilterButton.Enabled = false;
            }
            else
            {
                startFilterButton.Enabled = true;
                addFolderButton.Enabled = true;
                addAppButton.Enabled = true;

                stopFilterButton.Enabled = false;
                refreshSettingsButton.Enabled = false;
            }

            switch (status)
            {
                case NetFilterStatus.NotStarted:
                    filterStatusLabel.Text = "Filter not ready";
                    startFilterButton.Enabled = false;
                    break;

                case NetFilterStatus.Ready:
                    filterStatusLabel.Text = "Filter ready to start";
                    break;

                case NetFilterStatus.Started:
                    filterStatusLabel.Text = "Filter started..";
                    break;

                case NetFilterStatus.Stopped:
                    filterStatusLabel.Text = "Filter stopped..";
                    break;

                case NetFilterStatus.Failed:
                    filterStatusLabel.Text = "Filter fails during start..";
                    break;
            }
        }

        private void AddDirectoryHandler()
        {
            DialogResult dialogResult = folderBrowserDialog.ShowDialog();
            if (dialogResult == DialogResult.OK)
            {
                AddDirectory(folderBrowserDialog.SelectedPath);
                updateFormItems(NetFilterStatus.Ready);
            }
        }

        private void AddProcessHandler()
        {
            DialogResult dialogResult = openFileDialog.ShowDialog();
            {
                if (dialogResult == DialogResult.OK)
                {
                    foreach (var process in openFileDialog.FileNames)
                    {
                        AddProcess(process);
                        updateFormItems(NetFilterStatus.Ready);
                    }
                }
            }
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            if (!isInit)
            {
                // write to log
                logger.write("Couldn't create netMon instance");
                MessageBox.Show("Couldn't create netMon instance", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }

            updateFormItems(NetFilterStatus.NotStarted);

            // Read settings
            if (!settings.read())
            {
                // write to log
                logger.write("Couldn't read settings");
                return;
            }

            foreach (var process in settings.TracingProcesses)
            {
                AddProcess(process, addToSettings:false);
            }

            generateSelfsignedCertificateToolStripMenuItem.Checked = settings.CertSelfSigned;

            updateFormItems(NetFilterStatus.Ready);
            settingsStatusLabel.Text = "Settings readed..";
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (isInit)
            {
                DialogResult dialogResult = MessageBox.Show("Do you wanna exit?", "Quit",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dialogResult == DialogResult.No)
                {
                    e.Cancel = true;
                }
            }
        }
        #endregion

        void ShowLog()
        {
            logForm.OpenLogPath("");
            logForm.Visible = true;
        }

        #region Button handlers

        private void startFilterButton_Click(object sender, EventArgs e)
        {
            StartNetFilter();
        }

        private void stopFilterButton_Click(object sender, EventArgs e)
        {
            StopNetfilter();
        }

        private void addFolderButton_Click(object sender, EventArgs e)
        {
            AddDirectoryHandler();
        }

        private void addAppButton_Click(object sender, EventArgs e)
        {
            AddProcessHandler();
        }

        private void refreshSettingsButton_Click(object sender, EventArgs e)
        {
            RefreshSettings();
        }

        #endregion

        #region Tree view handlers

        private void filteredAppsTreeView_DragDrop(object sender, DragEventArgs e)
        {
            string[] draggedItems = (string[])e.Data.GetData(DataFormats.FileDrop, true);
            foreach (string item in draggedItems)
            {
                FileAttributes attr = File.GetAttributes(item);
                if ((attr & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    AddDirectory(item);
                    updateFormItems(NetFilterStatus.Ready);
                }
                else if (Path.GetExtension(item) == ".exe")
                {
                    if (!settings.isExistsTracingProcess(item))
                    {
                        AddProcess(item);
                        updateFormItems(NetFilterStatus.Ready);
                    }
                }
            }

            List<TreeNode> emptyDirectoryNodes = new List<TreeNode>();
            GetEmptyDirectoryNodes(root, ref emptyDirectoryNodes);
            while (emptyDirectoryNodes.Count > 0)
            {
                foreach (var removeItem in emptyDirectoryNodes)
                {
                    removeItem.Remove();
                }
                emptyDirectoryNodes.Clear();
                GetEmptyDirectoryNodes(root, ref emptyDirectoryNodes);
            }
        }

        private void filteredAppsTreeView_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }

        }

        #endregion

        private void toolStripMenuItemDeleteFolder_Click(object sender, EventArgs e)
        {
            DeleteSelectedTreeItem(filteredAppsTreeView.SelectedNode);
        }

        private void toolStripMenuItemDeleteFile_Click(object sender, EventArgs e)
        {
            DeleteSelectedTreeItem(filteredAppsTreeView.SelectedNode);
        }

        private void filteredAppsTreeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                filteredAppsTreeView.SelectedNode = filteredAppsTreeView.GetNodeAt(e.X, e.Y);
            }
        }

        #region Main menu handlers
        private void startToolStripMenuItem_Click(object sender, EventArgs e)
        {
            StartNetFilter();
        }

        private void stopToolStripMenuItem_Click(object sender, EventArgs e)
        {
            StopNetfilter();
        }

        private void refreshSettingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            RefreshSettings();
        }

        private void generateSelfsignedCertificateToolStripMenuItem_Click(object sender, EventArgs e)
        {
            bool certSelfSigned = generateSelfsignedCertificateToolStripMenuItem.Checked;
            settings.CertSelfSigned = certSelfSigned;
            generateSelfsignedCertificateToolStripMenuItem.Checked = !certSelfSigned;
        }

        private void applicationLogToolStripMenuItem_Click(object sender, EventArgs e)
        {
            logForm.OpenLogPath(logger.LogPath);
        }

        private void netfilterLogToolStripMenuItem_Click(object sender, EventArgs e)
        {
            uint bufSize = 1024;
            byte[] buf = new byte[bufSize];

            NetFilterWrap.NetMonLogPath(netMon, buf, bufSize);

            string logPath = System.Text.Encoding.ASCII.GetString(buf).Split('\0')[0];
            logForm.OpenLogPath(logPath);
        }

        private void addCatalogToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AddDirectoryHandler();
        }

        private void addAppToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AddProcessHandler();
        }

        private void clearListToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<TreeNode> removeNodeList = new List<TreeNode>();
            foreach (TreeNode child in root.Nodes)
            {
                removeNodeList.Add(child);
            }

            foreach (var removeNode in removeNodeList)
            {
                removeNode.Remove();
            }
                  
            settings.clearTracingProcessList();
            updateFormItems(NetFilterStatus.NotStarted);
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }
        #endregion
    }
}
