﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Reflection;
using System.Net;

using SC4Parser.DataStructures;
using SC4Parser.Files;
using SC4Parser.Types;
using SC4Parser.Subfiles;
using SC4Parser;
using SC4Parser.Logging;
using System.Diagnostics;

namespace SC4CartographerUI
{
    public partial class MainForm : Form
    {
        struct Map
        {
            public SC4SaveFile Save;
            public MapCreationParameters Parameters;
        }

        string RootSimCitySavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", 
            "SimCity 4", 
            "Regions");

        private Map map = new Map();
        private Bitmap previewNormalMapBitmap;
        private Bitmap previewZoomedMapBitmap;
        private bool previewZoomed = false;

        private RichTextBoxLogger logger = null;
        private FileLogger fileLogger = null;

        // Locally cached data from our currently loaded save game
        // Used when getting pixel data from map
        // (saves excessive logging calls that happen when using map.Save directly)
        private float[][] terrainData = null;
        private List<Lot> zoneData = null;

        public MainForm()
        {
            InitializeComponent();

            //propertiesForm = new PropertiesForm(map.Parameters, this);

            //logger = new RichTextBoxLogger(LogTextBox);
            fileLogger = new FileLogger();

            map.Parameters = new MapCreationParameters();

            //map.Parameters.VisibleMapObjects = new List<MapObject>() { MapObject.AirportZone, MapObject.MilitaryZone };
            //map.Parameters.SaveToFile("test.sc4cart");
            //map.Parameters.LoadFromFile("test.sc4cart");

            SetAppearanceUIValuesUsingParameters(map.Parameters);
            RegisterAppearanceEvents();

        }
        public MainForm(string path) : this()
        {
            // Try and load parameters from path if they have been given to program
            // (this is called when an associated file [.sc4cart] is used to call program)
            LoadMapParameters(path);
        }

        #region Core functionality
        #endregion

        #region Preview and Save game functionality
        // TODO: Seperate out

        /// <summary>
        /// Sets map creation parameters and refreshes preview
        /// </summary>
        /// <param name="parameters"></param>
        public void SetAndUpdateMapCreationParameters(MapCreationParameters parameters)
        {
            map.Parameters = parameters;

            GenerateMapPreview();

            // Call garbage collector to cleanup anything left over from generating new preview
            // gets a bit spammy sometimes.... man modern constructs like GC have made me weak
            // and this is almost certainly not a good move
            // but.....
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        /// <summary>
        /// Generates and sets preview map image on form
        /// </summary>
        public void GenerateMapPreview()
        {
            // Generate normal preview image
            MapCreationParameters normalMapPreviewParameters = new MapCreationParameters(map.Parameters);
            previewNormalMapBitmap = MapRenderer.CreateMapBitmap(map.Save, normalMapPreviewParameters);

            // Don't bother rendering a zoomed in map if the segment size is bigger than 10 pixels (zoomed in map size)
            // We perform the same check in TogglePreviewImage()
            if (map.Parameters.GridSegmentSize <= 10)
            {
                // Generate zoomed preview image
                MapCreationParameters zoomedMapPreviewParameters = new MapCreationParameters(map.Parameters);
                zoomedMapPreviewParameters.GridSegmentSize = 10;
                zoomedMapPreviewParameters.SegmentPaddingX = 4;
                zoomedMapPreviewParameters.SegmentPaddingY = 4;
                previewZoomedMapBitmap = MapRenderer.CreateMapBitmap(map.Save, zoomedMapPreviewParameters);

                // Change cursor to show we can zoom in
                MapPictureBox.Cursor = Cursors.Cross;
            }
            else
            {
                // Reset the cursor to whatever the user is currently using
                MapPictureBox.Cursor = Cursors.Default;
            }

            // Set image, reset zoom
            MapPictureBox.SizeMode = PictureBoxSizeMode.AutoSize;
            CenterPictureBox(MapPictureBox, previewNormalMapBitmap);
            previewZoomed = false;

            // Setup toolstrip details
            Process proc = Process.GetCurrentProcess();
            MemoryUsedToolStripStatusLabel.Text = $"Memory used: {Math.Truncate(Helper.ConvertBytesToMegabytes(proc.PrivateMemorySize64)).ToString()} MB";
            MapSizeToolStripStatusLabel.Text = $"Size: {previewNormalMapBitmap.Width.ToString()} x {previewNormalMapBitmap.Height.ToString()}px";
        }

        /// <summary>
        /// Check if a save has the subfile we need, if it does then we can load it
        /// if not avoid it otherwise SC4Parsernwill throw an exception
        /// </summary>
        public bool CheckSaveCanLoad(string path)
        {
            try
            {
                SC4SaveFile save = new SC4SaveFile(path);
                save.GetLotSubfile();
                return true;
            }
            catch (Exception)
            {
                return false;
            } 
        }

        /// <summary>
        /// Common function to load a save game and create a map from it
        /// </summary>
        /// <param name="path"></param>
        public void LoadSaveGame(string path)
        {
            SC4SaveFile save = null;

            // Load the save file
            try
            {
                save = new SC4SaveFile(path);
            }
            catch (DBPFParsingException e)
            {
                var errorForm = new ErrorForm(
                    "Error loading save game",
                    $"Could not load save game '{Path.GetFileName(path)}'.",
                    e,
                    true);
                errorForm.StartPosition = FormStartPosition.CenterParent;
                errorForm.ShowDialog();

                return;
            }

            // see if lots subfile exists
            try
            {
                save.GetLotSubfile();
            }
            catch (SubfileNotFoundException e)
            {
                var errorForm = new ErrorForm(
                    "Error loading save game",
                    $"Could not create map for '{Path.GetFileName(path)}'. Could not load zone data or it does not exist.",
                    e,
                    true);

                errorForm.StartPosition = FormStartPosition.CenterParent;
                errorForm.ShowDialog();

                return;
            }

            // Save seems to load alright, copy it over to out map creation parameters
            map.Save = save;

            try
            {
                // Generate and set map preview images
                GenerateMapPreview();
            }
            catch (SubfileNotFoundException e)
            {
                var errorForm = new ErrorForm(
                    "Error creating preview",
                    $"Could not create preview map for '{Path.GetFileName(path)}'.",
                    e,
                    true);

                errorForm.StartPosition = FormStartPosition.CenterParent;
                errorForm.ShowDialog();

                return;
            }

            // Cache some save data for map pixel lookup
            terrainData = save.GetTerrainMapSubfile().Map;
            zoneData = save.GetLotSubfile().Lots;

            // Set window title
            this.Text = "SC4Cartographer - '" + Path.GetFileName(path) + "'";
                
            EnableSaveButtons();

            // Call garbage collector to cleanup anything left over from last load
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        public string GenerateDefaultMapFilename()
        {
            string savefile = Path.GetFileNameWithoutExtension(map.Save.FilePath);
            savefile = savefile.Replace("City - ", "");
            return savefile;
        }

        /// <summary>
        /// Common function that saves out a map to a file
        /// </summary>
        public void SaveMap(string path, string name)
        {
            string filePath = Path.Combine(path, name);

            // Get current extension
            string extension = "";
            switch (map.Parameters.OutputFormat)
            {
                case OutFormat.PNG:
                    extension = ".png";
                    break;
                case OutFormat.JPEG:
                    extension = ".jpg";
                    break;
            }

            string currentFilename = filePath + extension;
            currentFilename = Helper.GenerateFilename(currentFilename);

            try
            {
                // Get the bitmap (this time we actually generate it from what the user inputted
                // not what we needed when we were generating the preview)
                Bitmap outBitmap = MapRenderer.CreateMapBitmap(map.Save, map.Parameters);

                // Actually save out the image
                switch (map.Parameters.OutputFormat)
                {
                    case OutFormat.PNG:
                        outBitmap.Save(currentFilename, ImageFormat.Png);
                        break;
                    case OutFormat.JPEG:
                        outBitmap.Save(currentFilename, ImageFormat.Jpeg);
                        break;
                }

                // Show form when successfully created
                var mapCreatedForm = new SuccessForm(
                    "Map Saved",
                    $"Map '{Path.GetFileName(currentFilename)}' has been successfully saved to",
                    Path.GetDirectoryName(currentFilename),
                    currentFilename);

                mapCreatedForm.StartPosition = FormStartPosition.CenterParent;
                mapCreatedForm.ShowDialog();

            }
            catch (Exception e)
            {
                var errorForm = new ErrorForm(
                    "Error saving map",
                    $"There was an error while trying to save a map for '{path}'.",
                    e,
                    true);

                errorForm.StartPosition = FormStartPosition.CenterParent;
                errorForm.ShowDialog();
            }

            // Cleanup any stuff after saving (these bitmaps can take up a fair amount of memory)
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        public void SaveMapParametersWithDialog()
        {
            // Create generic name at current directory
            string filePath = Path.Combine(Directory.GetCurrentDirectory(), "map_appearance.sc4cart");
            filePath = Helper.GenerateFilename(filePath);

            using (SaveFileDialog fileDialog = new SaveFileDialog())
            {
                fileDialog.Title = "Save SC4Cartographer map properties";
                fileDialog.InitialDirectory = Directory.GetCurrentDirectory();
                fileDialog.FileName = Path.GetFileName(filePath);
                fileDialog.RestoreDirectory = true;
                //fileDialog.CheckFileExists = true;
                fileDialog.CheckPathExists = true;
                fileDialog.Filter = "SC4Cartographer properties file (*.sc4cart)|*.sc4cart";
                if (fileDialog.ShowDialog(this) == DialogResult.OK)
                {
                    SaveMapParameters(fileDialog.FileName);
                }
            }
        }

        /// <summary>
        /// Common function called when saving map parameters/properties/appearance to a file
        /// </summary>
        /// <param name="path"></param>
        public void SaveMapParameters(string path)
        {
            try
            {
                map.Parameters.SaveToFile(path);

                var successForm = new SuccessForm(
                    "Map appearance saved",
                    $"Map appearance file '{Path.GetFileName(path)}' has been successfully saved to",
                    Path.GetDirectoryName(path),
                    path);

                successForm.StartPosition = FormStartPosition.CenterParent;
                successForm.ShowDialog();
            }
            catch (Exception ex)
            {
                ErrorForm form = new ErrorForm(
                    "Could not save map properties",
                    $"An error occured while trying to save map properties file ({path})",
                    ex,
                    false);

                form.StartPosition = FormStartPosition.CenterParent;
                form.ShowDialog();
            }
        }

        /// <summary>
        /// Common function called when loading map parameters/properties/appearance from file
        /// </summary>
        /// <param name="path"></param>
        public void LoadMapParameters(string path)
        {
            try
            {
                // Try and load parameters from a file
                map.Parameters.LoadFromFile(path);

                // Populate appearance ui items with new parameters
                SetAppearanceUIValuesUsingParameters(map.Parameters);
            }
            catch (Exception ex)
            {
                ErrorForm form = new ErrorForm(
                    "Could not load map properties",
                    $"An error occured while trying to load map properties from file ({path})",
                    ex,
                    false);

                form.StartPosition = FormStartPosition.CenterParent;
                form.ShowDialog();

                return;
            }
        }

        /// <summary>
        /// Switch between zoomed and normal images
        /// </summary>
        public void TogglePreviewImage()
        {
            previewZoomed = !previewZoomed;

            // Don't show preview image if the grid size is already bigger than the zoomed in size
            if (map.Parameters.GridSegmentSize > 10)
            {
                return;
            }

            if (previewZoomed)
            {
                CenterPictureBox(MapPictureBox, previewZoomedMapBitmap);
            }
            else
            {
                CenterPictureBox(MapPictureBox, previewNormalMapBitmap);
            }
        }

        /// <summary>
        /// We don't want these buttons to be enabled when nothing is loaded
        /// </summary>
        private void EnableSaveButtons()
        {
            saveToolStripMenuItem.Enabled = true;
            saveAsToolStripMenuItem.Enabled = true;
            SaveButton.Enabled = true;
            AppearanceGroupBox.Enabled = true;

            OpenTextLabel.Visible = false;
        }

        /// <summary>
        /// Rebuilds tree view and its contents
        /// </summary>
        private void RefreshTreeView()
        {
            // Clear the tree
            FileTreeView.Nodes.Clear();

            // If entered directory doesnt exist, dont bother rendering tree
            if (!Directory.Exists(SavePathTextbox.Text))
                return;

            // Get folders and files
            string[] dirs = Directory.GetDirectories(SavePathTextbox.Text);
            string[] files = Directory.GetFiles(SavePathTextbox.Text);

            foreach (string dir in dirs)
            {
                DirectoryInfo di = new DirectoryInfo(dir);
                TreeNode node = new TreeNode(di.Name, 0, 1);

                try
                {
                    node.Tag = dir;  //keep the directory's full path in the tag for use later

                    //if the directory has any sub directories add the place holder
                    if (di.GetFiles().Count() > 0 || di.GetDirectories().Count() > 0)//GetDirectories().Count() > 0) di.GetDirectories().Count() > 0)
                        node.Nodes.Add(null, "...", 0, 0);
                }
                catch (UnauthorizedAccessException)
                {
                    //if an unauthorized access exception occured display a locked folder
                    node.ImageIndex = 12;
                    node.SelectedImageIndex = 12;
                }
                catch (Exception ex)
                {
                    ErrorForm form = new ErrorForm(
                        "Directory tree error",
                        "An error occured while trying to populate save game file tree.",
                        ex,
                        false);

                    form.StartPosition = FormStartPosition.CenterParent;
                    form.ShowDialog();
                }
                finally
                {
                    FileTreeView.Nodes.Add(node);
                }
            }

            foreach (string file in files)
            {
                // Creat new node with file name
                TreeNode node = new TreeNode(Path.GetFileName(file), 0, 1);

                // Display file image on node
                node.ImageIndex = 13;
                node.SelectedImageIndex = 13;
                node.Tag = file;

                // Only show files with sc4 extension and don't show cities that haven't been
                // founded yet
                if (file.Contains(".sc4"))
                //&& !file.Contains("City - New City ("))
                {
                    if (FilterNewCitiesCheckbox.Checked
                        && file.Contains("City - New City ("))
                    {
                        continue;
                    }

                    // Add to node
                    FileTreeView.Nodes.Add(node);
                }
            }
        }

        /// <summary>
        /// Searches through a folder and returns a random SC4 savegame 
        /// </summary>
        /// <returns></returns>
        private string FindRandomSavegameFileInPath(string path)
        {
            Random rand = new Random();
            List<string> savegames = new List<string>();

            try
            {
                foreach (string dir in Directory.GetDirectories(path))
                {
                    foreach (string file in Directory.GetFiles(dir))
                    {
                        if (file.ToLower().Contains(".sc4")
                            && !file.Contains("City - New City")
                            && !file.Contains("Tutorial"))
                        {
                            savegames.Add(file);
                        }
                    }
                }
            }
            catch (Exception e) { }
            // TODO: Handle this exception, put in log

            return savegames[rand.Next(savegames.Count)];
        }

        /// <summary>
        /// Callback called when check for update (call to github to fetch info about latest release)
        /// has been performed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnUpdateChecked(object sender, DownloadStringCompletedEventArgs e)
        {
            // If we encounter an error then silently continue
            if (e.Error != null)
            {
                return;
            }

            UpdateInfo info = null;
            try
            {
                info = new UpdateInfo(e.Result);
            }
            catch (Exception) 
            {
                // Again this parser _might_ fail so we want to silently continue for an auto update
                // (oh well)
                return;
            }

            if (info.NewVersionAvailable)
            {
                var updateFormat = new UpdateForm(info);
                updateFormat.ShowDialog();
            }
        }

        /// <summary>
        /// Center bitmap inside picture box (amazing)
        /// Source: https://stackoverflow.com/a/9383029
        /// </summary>
        /// <param name="picBox"></param>
        /// <param name="picImage"></param>
        private void CenterPictureBox(PictureBox picBox, Bitmap picImage)
        {
            // Set image
            MapPictureBox.Image = picImage;

            // Center scroll bars
            panel1.AutoScrollPosition =
                new Point
                {
                    X = (MapPictureBox.Width - panel1.Width) / 2,
                    Y = (MapPictureBox.Height - panel1.Height) / 2
                };

            // Center image in picturebox
            picBox.Location = new Point((picBox.Parent.ClientSize.Width / 2) - (picImage.Width / 2),
                                        (picBox.Parent.ClientSize.Height / 2) - (picImage.Height / 2));
            picBox.Refresh();
        }

        /// <summary>
        /// Gets information for a specific pixel on the map. Returned as a string
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        private string GetMapPixelInfo(int x, int y)
        {
            string result = "";

            int cityX = 0;
            int cityY = 0;

            if (previewZoomed)
            {
                cityX = x / 10;
                cityY = y / 10;
            }
            else
            {
                // Work out coordinates on map
                cityX = x / map.Parameters.GridSegmentSize;
                cityY = y / map.Parameters.GridSegmentSize;
            }

            result = $"Mouse: {x}, {y}px (tile: {cityX}x, {cityY}z) ";

            try
            {
                result += $" (height: {terrainData[cityY][cityX]})";
            }
            catch (IndexOutOfRangeException) { } // Silently continue when we accidently get a range outside of the terrain map bounds 

            // See if there is any zone data on that segment
            foreach (Lot lot in zoneData)
            {
                for (int lotZ = lot.MinTileZ; lotZ <= lot.MaxTileZ; lotZ++)
                {
                    if (lotZ == cityY)
                    {
                        for (int lotX = lot.MinTileX; lotX <= lot.MaxTileX; lotX++)
                        {
                            if (lotX == cityX)
                            {
                                result += $" (zone: {SC4Parser.Constants.LOT_ZONE_TYPE_STRINGS[lot.ZoneType]} [{SC4Parser.Constants.LOT_ZONE_WEALTH_STRINGS[lot.ZoneWealth]}])";
                            }
                        }
                    }
                }
            }

            return result;
        }

        #endregion

        #region Savegames and Preview Callbacks
        // TODO: Seperate out

        private void SaveButton_Click(object sender, EventArgs e)
        {
            string name = GenerateDefaultMapFilename();
            SaveMap(map.Parameters.OutputPath, name);
        }

        /// <summary>
        /// When main form loads
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainForm_Load(object sender, EventArgs e)
        {
            // Load a random map on open
            //logger = new RichTextBoxLogger(LogTextBox);
            if (Directory.Exists(RootSimCitySavePath))
            {
                bool validSaveFound = false;
                string path = "";

                // Find a save that will load without errors (probably doesn't have a lot subfile :/)
                while (validSaveFound == false)
                {
                    SavePathTextbox.Text = RootSimCitySavePath;
                    path = FindRandomSavegameFileInPath(RootSimCitySavePath);
                    if (CheckSaveCanLoad(path))
                    {
                        validSaveFound = true;
                    }
                }

                // Found a good save, load it
                LoadSaveGame(path);
            }
            else
            {
                SavePathTextbox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            // Set current path as output path
            map.Parameters.OutputPath = Directory.GetCurrentDirectory();


            // Check for update on startup
            if (Properties.Settings.Default.IgnoreUpdatePrompts == false)
            {
                UpdateChecker.GetLatestReleaseInfoAsync(OnUpdateChecked);
            }
        }

        /// <summary>
        /// Populates FileTreeView when SavegamePathTextbox's text changes
        /// </summary>
        private void SavePathTextbox_TextChanged(object sender, EventArgs e)
        {
            // Clear the tree
            FileTreeView.Nodes.Clear();

            // If entered directory doesnt exist, dont bother rendering tree
            if (!Directory.Exists(SavePathTextbox.Text))
                return;

            // Get folders and files
            string[] dirs = Directory.GetDirectories(SavePathTextbox.Text);
            string[] files = Directory.GetFiles(SavePathTextbox.Text);

            foreach (string dir in dirs)
            {
                DirectoryInfo di = new DirectoryInfo(dir);
                TreeNode node = new TreeNode(di.Name, 0, 1);

                try
                {
                    node.Tag = dir;  //keep the directory's full path in the tag for use later

                    //if the directory has any sub directories add the place holder
                    if (di.GetFiles().Count() > 0 || di.GetDirectories().Count() > 0)//GetDirectories().Count() > 0) di.GetDirectories().Count() > 0)
                        node.Nodes.Add(null, "...", 0, 0);
                }
                catch (UnauthorizedAccessException)
                {
                    //if an unauthorized access exception occured display a locked folder
                    node.ImageIndex = 12;
                    node.SelectedImageIndex = 12;
                }
                catch (Exception ex)
                {
                    ErrorForm form = new ErrorForm(
                        "Directory tree error",
                        "An error occured while trying to populate save game file tree.",
                        ex,
                        false);

                    form.StartPosition = FormStartPosition.CenterParent;
                    form.ShowDialog();
                }
                finally
                {
                    FileTreeView.Nodes.Add(node);
                }
            }

            foreach (string file in files)
            {
                // Creat new node with file name
                TreeNode node = new TreeNode(Path.GetFileName(file), 0, 1);

                // Display file image on node
                node.ImageIndex = 13;
                node.SelectedImageIndex = 13;
                node.Tag = file;

                // Only show files with sc4 extension and don't show cities that haven't been
                // founded yet
                if (file.Contains(".sc4"))
                    //&& !file.Contains("City - New City ("))
                {
                    if (FilterNewCitiesCheckbox.Checked 
                        && file.Contains("City - New City"))
                    {
                        continue;
                    }

                    // Add to node
                    FileTreeView.Nodes.Add(node);
                }
            }
        }

        /// <summary>
        /// Generates a list of files and folders and adds them to the FileTreeView as nodes are expanded
        /// </summary>
        private void FileTreeView_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Node.Nodes.Count > 0)
            {
                if (e.Node.Nodes[0].Text == "..." && e.Node.Nodes[0].Tag == null)
                {
                    e.Node.Nodes.Clear();

                    // get the list of sub directories & files
                    string[] dirs = Directory.GetDirectories(e.Node.Tag.ToString());
                    string[] files = Directory.GetFiles(e.Node.Tag.ToString());

                    foreach (string dir in dirs)
                    {
                        DirectoryInfo di = new DirectoryInfo(dir);
                        TreeNode node = new TreeNode(di.Name, 0, 1);

                        try
                        {
                            //keep the directory's full path in the tag for use later
                            node.Tag = dir;

                            //if the directory has any sub directories add the place holder
                            if (di.GetFiles().Count() > 0 || di.GetDirectories().Count() > 0)//GetDirectories().Count() > 0)
                                node.Nodes.Add(null, "...", 0, 0);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            //if an unauthorized access exception occured display a locked folder
                            node.ImageIndex = 12;
                            node.SelectedImageIndex = 12;
                        }
                        catch (Exception ex)
                        {
                            ErrorForm form = new ErrorForm(
                                "Directory tree error",
                                "An error occured while trying to populate save game file tree.",
                                ex,
                                false);

                            form.StartPosition = FormStartPosition.CenterParent;
                            form.ShowDialog();
                        }
                        finally
                        {
                            e.Node.Nodes.Add(node);
                        }
                    }

                    foreach (string file in files)
                    {
                        // Creat new node with file name
                        TreeNode node = new TreeNode(Path.GetFileName(file), 0, 1);

                        // Display file image on node
                        node.ImageIndex = 13;
                        node.SelectedImageIndex = 13;
                        node.Tag = file;

                        // Only show files with sc4 extension and don't show cities that haven't been
                        // founded yet
                        if (file.Contains(".sc4") )
                            //&& !file.Contains("City - New City (")
                        {
                            if (FilterNewCitiesCheckbox.Checked
                                && file.Contains("City - New City"))
                            {
                                continue;
                            }

                            // Add to node
                            e.Node.Nodes.Add(node);
                        }
                    }
                }
            }
        }

        private void LogTextBox_TextChanged(object sender, EventArgs e)
        {
            // Set caret position to end of current text
            //LogTextBox.SelectionStart = LogTextBox.Text.Length;

            // Scroll to bottom automatically
            //LogTextBox.ScrollToCaret();
        }

        private void PropertiesButton_Click(object sender, EventArgs e)
        {

            // Generate map again
            //LoadSaveGame(mapCreationParameters.SaveFile.FilePath);
        }

        /// <summary>
        /// Load a file from the tree view
        /// </summary>
        private void FileTreeView_OnNodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            // Check the node we have clicked on is a file
            // (check the image index that we have set earlier, this is the easiest way)
            if (e.Node.ImageIndex == 13)
            {
                
                LoadSaveGame((string) e.Node.Tag);

            }
        }

        private void FileBrowserButton_Click(object sender, EventArgs e)
        {
            // Create folder browser dialog
            using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
            {
                folderDialog.SelectedPath = SavePathTextbox.Text;
                if (folderDialog.ShowDialog(this) == DialogResult.OK)
                    SavePathTextbox.Text = folderDialog.SelectedPath;
            }
        }

        private void MapPictureBox_Clicked(object sender, EventArgs e)
        {
            TogglePreviewImage();
        }

        private void saveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string name = GenerateDefaultMapFilename();
            SaveMap(map.Parameters.OutputPath, name);
        }

        private void saveAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog fileDialog = new SaveFileDialog())
            {
                fileDialog.Title = "Save SimCity 4 map";
                fileDialog.InitialDirectory = Directory.GetCurrentDirectory();
                fileDialog.RestoreDirectory = true;
                //fileDialog.CheckFileExists = true;
                fileDialog.CheckPathExists = true;
                //fileDialog.Filter = "Simcity 4 save files (*.sc4)|*.sc4";
                if (fileDialog.ShowDialog(this) == DialogResult.OK)
                {
                    SaveMap(Path.GetDirectoryName(fileDialog.FileName), Path.GetFileNameWithoutExtension(fileDialog.FileName));
                }
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Environment.Exit(0);
        }

        private void savegameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog fileDialog = new OpenFileDialog())
            {
                fileDialog.Title = "Load SimCity 4 save game";
                fileDialog.InitialDirectory = SavePathTextbox.Text;
                fileDialog.RestoreDirectory = true;
                fileDialog.CheckFileExists = true;
                fileDialog.CheckPathExists = true;
                fileDialog.Filter = "Simcity 4 save file (*.sc4)|*.sc4";
                if (fileDialog.ShowDialog(this) == DialogResult.OK)
                    LoadSaveGame(fileDialog.FileName);
            }
        }

        private void folderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Create folder browser dialog
            using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
            {
                folderDialog.SelectedPath = SavePathTextbox.Text;
                if (folderDialog.ShowDialog(this) == DialogResult.OK)
                    SavePathTextbox.Text = folderDialog.SelectedPath;
            }
        }

        private void editToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var mapCreatedForm = new AboutBox();
            mapCreatedForm.StartPosition = FormStartPosition.CenterParent;
            mapCreatedForm.ShowDialog();
        }

        private void FilterNewCitiesCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            RefreshTreeView();
        }

        private void reportABugToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            string version = Assembly.GetExecutingAssembly().GetName().Name + " v" + v.Major + "." + v.Minor + "." + v.Build + " (r" + v.Revision + ") ";
            version = version.Replace(' ', '+');
            string parserVersion = "SC4Parser+v1.0.0.0";

            string issueLink = @"https://github.com/killeroo/SC4Cartographer/issues/new?body=%0A%0A%0A---------%0A" + version + "%0A" + parserVersion;
            System.Diagnostics.Process.Start(issueLink);
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var mapCreatedForm = new AboutBox();
            mapCreatedForm.StartPosition = FormStartPosition.CenterParent;
            mapCreatedForm.ShowDialog();
        }
       
        private void projectWebpageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start(@"https://github.com/killeroo/SC4Cartographer");
        }
       
        private void toolStripMenuItem6_Click(object sender, EventArgs e)
        {
            var mapCreatedForm = new LogForm(fileLogger.LogPath, fileLogger.Created);
            mapCreatedForm.StartPosition = FormStartPosition.CenterParent;
            mapCreatedForm.ShowDialog();
        }

        private void UpdatesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            UpdateInfo info = null;

            try
            {
                // Try and fetch latest release info from github
                info = UpdateChecker.GetLatestReleaseInfo();
            }
            catch (Exception ex)
            {
                var errorForm = new ErrorForm(
                    "Error fetching SC4Cartographer update info",
                    $"Could not get current release information from github.",
                    ex, 
                    false);
                errorForm.StartPosition = FormStartPosition.CenterParent;
                errorForm.ShowDialog();

                return;
            }

            if (info.NewVersionAvailable)
            {
                var updateFormat = new UpdateForm(info);
                updateFormat.ShowDialog();
            }
            else
            {

                var successForm = new SuccessForm(
                    "Up to date!",
                    "You are using the most recent version of SC4Cartographer.", 
                    "");

                successForm.StartPosition = FormStartPosition.CenterParent;
                successForm.ShowDialog();
            }
        }

        private void toolStripMenuItem8_Click(object sender, EventArgs e)
        {
            SaveMapParametersWithDialog();
        }

        private void toolStripMenuItem7_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog fileDialog = new OpenFileDialog())
            {
                fileDialog.Title = "Load SC4Cartographer map properties";
                fileDialog.InitialDirectory = Directory.GetCurrentDirectory();
                fileDialog.RestoreDirectory = true;
                fileDialog.CheckFileExists = true;
                fileDialog.CheckPathExists = true;
                fileDialog.Filter = "SC4Cartographer properties file (*.sc4cart)|*.sc4cart";
                if (fileDialog.ShowDialog(this) == DialogResult.OK)
                {
                    // Load new parameters and regenerate preview
                    LoadMapParameters(fileDialog.FileName);
                    GenerateMapPreview();
                }
            }
        }

        private void MapPictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            MousePositionToolStripStatusLabel.Text = GetMapPixelInfo(e.X, e.Y);
        }

        private void MapPictureBox_MouseLeave(object sender, EventArgs e)
        {
            MousePositionToolStripStatusLabel.Text = "";
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (previewZoomed)
            {
                CenterPictureBox(MapPictureBox, previewZoomedMapBitmap);
            }
            else
            {
                CenterPictureBox(MapPictureBox, previewNormalMapBitmap);
            }
        }

        #endregion


        #region Appearance Group Functionality

        /// <summary>
        /// We seperated out registering events from their components creation so we can set the UI values without having 
        /// their callbacks fire
        /// </summary>
        private void RegisterAppearanceEvents()
        {
            this.ShowZoneOutlinesCheckbox.CheckedChanged += new System.EventHandler(this.ShowZoneOutlinesCheckbox_CheckedChanged);
            this.SegmentOffsetNumericUpDown.ValueChanged += new System.EventHandler(this.SegmentOffsetNumericUpDown_ValueChanged);
            this.SegmentPaddingNumericUpDown.ValueChanged += new System.EventHandler(this.SegmentPaddingNumericUpDown_ValueChanged);
            this.GridSegmentSizeNumericUpDown.ValueChanged += new System.EventHandler(this.GridSegmentSizeNumericUpDown_ValueChanged);
            this.ShowGridLinesCheckbox.CheckedChanged += new System.EventHandler(this.ShowGridLinesCheckbox_CheckedChanged);
            this.SpaceportEditButton.Click += new System.EventHandler(this.SpaceportEditButton_Click);
            this.SeaportsEditButton.Click += new System.EventHandler(this.SeaportsEditButton_Click);
            this.AirportsEditButton.Click += new System.EventHandler(this.AirportsEditButton_Click);
            this.MilitaryEditButton.Click += new System.EventHandler(this.MilitaryEditButton_Click);
            this.ZoneOutlinesEditButton.Click += new System.EventHandler(this.ZoneOutlinesEditButton_Click);
            this.IndustrialZoneLowEditButton.Click += new System.EventHandler(this.IndustrialZoneLowEditButton_Click);
            this.IndustrialZoneMidEditButton.Click += new System.EventHandler(this.IndustrialZoneMidEditButton_Click);
            this.IndustrialZoneHighEditButton.Click += new System.EventHandler(this.IndustrialZoneHighEditButton_Click);
            this.CommercialZoneHighEditButton.Click += new System.EventHandler(this.CommercialZoneHighEditButton_Click);
            this.CommercialZoneMidEditButton.Click += new System.EventHandler(this.CommercialZoneMidEditButton_Click);
            this.CommercialZoneLowEditButton.Click += new System.EventHandler(this.CommercialZoneLowEditButton_Click);
            this.GridLinesEditTextbox.Click += new System.EventHandler(this.GridLinesEditTextbox_Click);
            this.BuildingsEditButton.Click += new System.EventHandler(this.BuildingsEditButton_Click);
            this.ResidentialZoneLowEditButton.Click += new System.EventHandler(this.ResidentialZoneLowEditButton_Click);
            this.ResidentialZoneHighEditButton.Click += new System.EventHandler(this.ResidentialZoneHighEditButton_Click);
            this.ResidentialZoneMidEditButton.Click += new System.EventHandler(this.ResidentialZoneMidEditButton_Click);
            this.GridBackgroundEditButton.Click += new System.EventHandler(this.GridBackgroundEditButton_Click);
        }

        private void SetAppearanceUIValuesUsingParameters(MapCreationParameters parameters)
        {
            GridBackgroundTextbox.BackColor = parameters.ColorDictionary[MapColorObject.Background];
            GridLinesTextbox.BackColor = parameters.ColorDictionary[MapColorObject.GridLines];
            BuildingsTextbox.BackColor = parameters.ColorDictionary[MapColorObject.PloppedBuilding];
            ZoneOutlinesTextbox.BackColor = parameters.ColorDictionary[MapColorObject.ZoneOutline];
            MilitaryTextbox.BackColor = parameters.ColorDictionary[MapColorObject.Military];
            AirportsTextbox.BackColor = parameters.ColorDictionary[MapColorObject.Airport];
            SeaportTextbox.BackColor = parameters.ColorDictionary[MapColorObject.Seaport];
            SpaceportTextbox.BackColor = parameters.ColorDictionary[MapColorObject.Spaceport];
            ResidentialZoneLowTextbox.BackColor = parameters.ColorDictionary[MapColorObject.ResidentialLow];
            ResidentialZoneMidTextbox.BackColor = parameters.ColorDictionary[MapColorObject.ResidentialMid];
            ResidentialZoneHighTextbox.BackColor = parameters.ColorDictionary[MapColorObject.ResidentialHigh];
            CommercialZoneLowTextbox.BackColor = parameters.ColorDictionary[MapColorObject.CommercialLow];
            CommercialZoneMidTextbox.BackColor = parameters.ColorDictionary[MapColorObject.CommercialMid];
            CommercialZoneHighTextbox.BackColor = parameters.ColorDictionary[MapColorObject.CommercialHigh];
            IndustrialZoneLowTextbox.BackColor = parameters.ColorDictionary[MapColorObject.IndustrialLow];
            IndustrialZoneMidTextbox.BackColor = parameters.ColorDictionary[MapColorObject.IndustrialMid];
            IndustrialZoneHighTextbox.BackColor = parameters.ColorDictionary[MapColorObject.IndustrialHigh];

            GridSegmentSizeNumericUpDown.Value = parameters.GridSegmentSize;
            SegmentPaddingNumericUpDown.Value = parameters.SegmentPaddingX;
            SegmentOffsetNumericUpDown.Value = parameters.SegmentOffsetX;
            ShowGridLinesCheckbox.Checked = parameters.ShowGridLines;
            ShowZoneOutlinesCheckbox.Checked = parameters.ShowZoneOutlines;

            OutputPathTextbox.Text = parameters.OutputPath;
            if (parameters.OutputFormat == OutFormat.PNG)
            {
                PNGRadioButton.Checked = true;
                JPEGRadioButton.Checked = false;
            }
            else
            {
                PNGRadioButton.Checked = false;
                JPEGRadioButton.Checked = true;
            }

            VisibleObjectsTreeView.AfterCheck -= VisibleObjectsTreeView_AfterCheck;
            PopulateLayersTreeView(VisibleObjectsTreeView.Nodes, parameters.VisibleMapObjects);
            VisibleObjectsTreeView.ExpandAll();
            VisibleObjectsTreeView.AfterCheck += VisibleObjectsTreeView_AfterCheck;

        }
        private MapCreationParameters GetParametersFromAppearanceUIValues()
        {
            MapCreationParameters parameters = new MapCreationParameters();

            parameters.ColorDictionary[MapColorObject.Background] = GridBackgroundTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.GridLines] = GridLinesTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.PloppedBuilding] = BuildingsTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.ZoneOutline] = ZoneOutlinesTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.Military] = MilitaryTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.Airport] = AirportsTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.Seaport] = SeaportTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.Spaceport] = SpaceportTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.ResidentialLow] = ResidentialZoneLowTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.ResidentialMid] = ResidentialZoneMidTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.ResidentialHigh] = ResidentialZoneHighTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.CommercialLow] = CommercialZoneLowTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.CommercialMid] = CommercialZoneMidTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.CommercialHigh] = CommercialZoneHighTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.IndustrialLow] = IndustrialZoneLowTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.IndustrialMid] = IndustrialZoneMidTextbox.BackColor;
            parameters.ColorDictionary[MapColorObject.IndustrialHigh] = IndustrialZoneHighTextbox.BackColor;

            parameters.GridSegmentSize = (int)GridSegmentSizeNumericUpDown.Value;
            parameters.SegmentPaddingX = (int)SegmentPaddingNumericUpDown.Value;
            parameters.SegmentPaddingY = (int)SegmentPaddingNumericUpDown.Value;
            parameters.SegmentOffsetX = (int)SegmentOffsetNumericUpDown.Value;
            parameters.SegmentOffsetY = (int)SegmentOffsetNumericUpDown.Value;
            parameters.ShowGridLines = ShowGridLinesCheckbox.Checked;
            parameters.ShowZoneOutlines = ShowZoneOutlinesCheckbox.Checked;

            parameters.OutputPath = OutputPathTextbox.Text;
            if (PNGRadioButton.Checked)
            {
                parameters.OutputFormat = OutFormat.PNG;
            }
            else
            {
                parameters.OutputFormat = OutFormat.JPEG;
            }

            parameters.VisibleMapObjects = ParseLayersTreeView(VisibleObjectsTreeView.Nodes);

            return parameters;
        }

        public void PopulateLayersTreeView(TreeNodeCollection nodes, List<MapObject> objects)
        {
            foreach (TreeNode node in nodes)
            {
                if (node.Nodes.Count != 0)
                {
                    PopulateLayersTreeView(node.Nodes, objects);
                }
                else
                {
                    switch (node.Tag)
                    {
                        case "ResidentialLowZone":
                            if (objects.Contains(MapObject.ResidentialLowZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "ResidentialMidZone":
                            if (objects.Contains(MapObject.ResidentialMidZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "ResidentialHighZone":
                            if (objects.Contains(MapObject.ResidentialHighZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "CommercialLowZone":
                            if (objects.Contains(MapObject.CommercialLowZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "CommercialMidZone":
                            if (objects.Contains(MapObject.CommercialMidZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "CommercialHighZone":
                            if (objects.Contains(MapObject.CommercialHighZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "IndustrialHighZone":
                            if (objects.Contains(MapObject.IndustrialHighZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "IndustrialMidZone":
                            if (objects.Contains(MapObject.IndustrialMidZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "IndustrialLowZone":
                            if (objects.Contains(MapObject.IndustrialLowZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "PloppedBuildingZone":
                            if (objects.Contains(MapObject.PloppedBuildingZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "MilitaryZone":
                            if (objects.Contains(MapObject.MilitaryZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "AirportZone":
                            if (objects.Contains(MapObject.AirportZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "SeaportZone":
                            if (objects.Contains(MapObject.SeaportZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "SpaceportZone":
                            if (objects.Contains(MapObject.SpaceportZone))
                            {
                                node.Checked = true;
                                CheckAllParents(node.Parent, true);
                            }
                            break;
                        case "TerrainMap":
                            if (objects.Contains(MapObject.TerrainMap))
                            {
                                node.Checked = true;
                            }
                            break;
                    }
                }
            }
        }

        public List<MapObject> ParseLayersTreeView(TreeNodeCollection nodes)
        {
            List<MapObject> objects = new List<MapObject>();

            foreach (TreeNode node in nodes)
            {
                if (node.Nodes.Count != 0)
                {
                    objects.AddRange(ParseLayersTreeView(node.Nodes));
                }
                else
                {
                    if (node.Checked)
                    {
                        switch (node.Tag)
                        {
                            case "ResidentialLowZone":
                                objects.Add(MapObject.ResidentialLowZone);
                                break;
                            case "ResidentialMidZone":
                                objects.Add(MapObject.ResidentialMidZone);
                                break;
                            case "ResidentialHighZone":
                                objects.Add(MapObject.ResidentialHighZone);
                                break;
                            case "CommercialLowZone":
                                objects.Add(MapObject.CommercialLowZone);
                                break;
                            case "CommercialMidZone":
                                objects.Add(MapObject.CommercialMidZone);
                                break;
                            case "CommercialHighZone":
                                objects.Add(MapObject.CommercialHighZone);
                                break;
                            case "IndustrialHighZone":
                                objects.Add(MapObject.IndustrialHighZone);
                                break;
                            case "IndustrialMidZone":
                                objects.Add(MapObject.IndustrialMidZone);
                                break;
                            case "IndustrialLowZone":
                                objects.Add(MapObject.IndustrialLowZone);
                                break;
                            case "PloppedBuildingZone":
                                objects.Add(MapObject.PloppedBuildingZone);
                                break;
                            case "MilitaryZone":
                                objects.Add(MapObject.MilitaryZone);
                                break;
                            case "AirportZone":
                                objects.Add(MapObject.AirportZone);
                                break;
                            case "SeaportZone":
                                objects.Add(MapObject.SeaportZone);
                                break;
                            case "SpaceportZone":
                                objects.Add(MapObject.SpaceportZone);
                                break;
                            case "TerrainMap":
                                objects.Add(MapObject.TerrainMap);
                                break;
                        }
                    }
                }
            }

            return objects;
        }

        private void CheckParent(TreeNode parent, bool check)
        {
            if (parent == null)
                return;

            parent.Checked = check;
        }

        private void CheckAllNodes(TreeNodeCollection nodes, bool check)
        {
            foreach (TreeNode node in nodes)
            {
                node.Checked = check;

                if (node.Nodes.Count != 0)
                {
                    CheckAllNodes(node.Nodes, check);
                }
            }
        }

        private void CheckAllParents(TreeNode parent, bool check)
        {
            parent.Checked = check;

            if (parent.Parent != null)
            {
                CheckAllParents(parent.Parent, check);
            }
        }

        private bool AreSiblingsChecked(TreeNodeCollection nodes)
        {
            bool isChecked = false;

            foreach (TreeNode node in nodes)
            {
                if (node.Checked)
                {
                    isChecked = true;
                }
            }

            return isChecked;
        }

        #endregion

        #region Appearance Group Callbacks

        private void GridBackgroundEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = GridBackgroundTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                GridBackgroundTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void GridLinesEditTextbox_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = GridLinesTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                GridLinesTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void BuildingsEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = BuildingsTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                BuildingsTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void ResidentialZoneLowEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = ResidentialZoneLowTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                ResidentialZoneLowTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void ResidentialZoneMidEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = ResidentialZoneMidTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                ResidentialZoneMidTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void ResidentialZoneHighEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = ResidentialZoneHighTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                ResidentialZoneHighTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void CommercialZoneLowEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = CommercialZoneLowTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                CommercialZoneLowTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void CommercialZoneMidEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = CommercialZoneMidTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                CommercialZoneMidTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void CommercialZoneHighEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = CommercialZoneHighTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                CommercialZoneHighTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void IndustrialZoneLowEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = IndustrialZoneLowTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                IndustrialZoneLowTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void IndustrialZoneMidEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = IndustrialZoneMidTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                IndustrialZoneMidTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void IndustrialZoneHighEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = IndustrialZoneHighTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                IndustrialZoneHighTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void ZoneOutlinesEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = ZoneOutlinesTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                ZoneOutlinesTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void MilitaryEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = MilitaryTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                MilitaryTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void AirportsEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = AirportsTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                AirportsTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void SeaportsEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = SeaportTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                SeaportTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void SpaceportEditButton_Click(object sender, EventArgs e)
        {
            colorDialog = new ColorDialog();
            colorDialog.Color = SeaportTextbox.BackColor;
            colorDialog.AllowFullOpen = true;
            colorDialog.FullOpen = true;
            //colorDialog.StartPosition = FormStartPosition.CenterParent;

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                SeaportTextbox.BackColor = colorDialog.Color;

                SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
            }
        }

        private void EditOutputPathButton_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
            {
                folderDialog.SelectedPath = OutputPathTextbox.Text;
                if (folderDialog.ShowDialog(this) == DialogResult.OK)
                    OutputPathTextbox.Text = folderDialog.SelectedPath;
            }
        }

        private void RestoreDefaultsButton_Click(object sender, EventArgs e)
        {
            MapCreationParameters pristineParameters = new MapCreationParameters();

            // Copy over the output path
            // TODO: Watch it....
            // TODO: need common method for resetting and setting ui
            pristineParameters.OutputPath = map.Parameters.OutputPath;

            SetAppearanceUIValuesUsingParameters(pristineParameters);
            SetAndUpdateMapCreationParameters(pristineParameters);
        }
        private void GridSegmentSizeNumericUpDown_ValueChanged(object sender, EventArgs e)
        {
            SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
        }

        private void SegmentPaddingNumericUpDown_ValueChanged(object sender, EventArgs e)
        {
            SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
        }

        private void SegmentOffsetNumericUpDown_ValueChanged(object sender, EventArgs e)
        {
            SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
        }

        private void ShowGridLinesCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
        }

        private void ShowZoneOutlinesCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
        }

        private void VisibleObjectsTreeView_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (e.Node.Parent != null)
            {
                VisibleObjectsTreeView.AfterCheck -= VisibleObjectsTreeView_AfterCheck;
                if (AreSiblingsChecked(e.Node.Parent.Nodes))
                {
                    CheckParent(e.Node.Parent, true);
                }
                else
                {
                    CheckParent(e.Node.Parent, false);
                }
                VisibleObjectsTreeView.AfterCheck += VisibleObjectsTreeView_AfterCheck;
            }
            if (e.Node.Nodes.Count != 0)
            {
                VisibleObjectsTreeView.AfterCheck -= VisibleObjectsTreeView_AfterCheck;
                CheckAllNodes(e.Node.Nodes, e.Node.Checked);
                VisibleObjectsTreeView.AfterCheck += VisibleObjectsTreeView_AfterCheck;
            }

            SetAndUpdateMapCreationParameters(GetParametersFromAppearanceUIValues());
        }

        #endregion
    }
}
