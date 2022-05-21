﻿using System;
using System.IO;
using System.Windows.Forms;

namespace SC4CartographerUI
{
    internal class MapAppearanceSaveLoadDialogs
    {
        private const string defaultFilename = "map_appearance.sc4cart";
        private const string FileFilter = "SC4Cartographer properties file (*.sc4cart)|*.sc4cart";
        private readonly MainForm mainForm;
        private readonly MapAppearanceSerializer serializer;
        
        public MapAppearanceSaveLoadDialogs(MainForm mainForm, MapAppearanceSerializer serializer)
        {
            this.mainForm = mainForm;
            this.serializer = serializer;
        }

        public void SaveMapParametersWithDialog(MapCreationParameters parameters)
        {
            // Create generic name at current directory
            string filePath = Path.Combine(Directory.GetCurrentDirectory(), defaultFilename);
            filePath = Helper.GenerateFilename(filePath);

            using (SaveFileDialog fileDialog = new SaveFileDialog())
            {
                fileDialog.Title = "Save SC4Cartographer map properties";
                fileDialog.InitialDirectory = Directory.GetCurrentDirectory();
                fileDialog.FileName = Path.GetFileName(filePath);
                fileDialog.RestoreDirectory = true;
                //fileDialog.CheckFileExists = true;
                fileDialog.CheckPathExists = true;
                fileDialog.Filter = FileFilter;
                if (fileDialog.ShowDialog(mainForm) == DialogResult.OK)
                {
                    TrySaveAndShowResults(parameters, fileDialog.FileName);
                }
            }
        }

        private void TrySaveAndShowResults(MapCreationParameters parameters, string path)
        {
            try
            {
                serializer.SaveToFile(parameters, path);

                var successForm = new SuccessForm(
                    "Map appearance saved",
                    $"Map appearance file '{Path.GetFileName(path)}' has been successfully saved to:",
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

        public void LoadMapParametersWithDialog()
        {
            using (OpenFileDialog fileDialog = new OpenFileDialog())
            {
                fileDialog.Title = "Load SC4Cartographer map properties";
                fileDialog.InitialDirectory = Directory.GetCurrentDirectory();
                fileDialog.RestoreDirectory = true;
                fileDialog.CheckFileExists = true;
                fileDialog.CheckPathExists = true;
                fileDialog.Filter = FileFilter;
                if (fileDialog.ShowDialog(mainForm) == DialogResult.OK)
                {
                    // Load new parameters and regenerate preview
                    TryLoad(fileDialog.FileName);

                    // Change cursor to indicate that we are working on the preview
                    mainForm.Cursor = Cursors.WaitCursor;

                    // Only update preview if a map is loaded 
                    if (mainForm.mapLoaded)
                        mainForm.GenerateMapPreviewBitmaps(false);

                    // Reset cursor 
                    mainForm.Cursor = Cursors.Default;
                }
            }
        }

        /// <summary>
        /// Common function called when loading map parameters/properties/appearance from file
        /// </summary>
        /// <param name="path"></param>
        public void TryLoad(string path)
        {
            try
            {
                var parameters = serializer.LoadFromFile(path);
                
                // Populate appearance ui items with new parameters
                mainForm.SetAppearanceUIValuesUsingParameters(parameters);
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
    }
}
