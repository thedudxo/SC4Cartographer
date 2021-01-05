﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.IO;
using System.Windows.Forms;

namespace SC4CartographerUI
{
    static class Helper
    {
        /// <summary>
        /// Extension method for determining build time
        /// </summary>
        /// <param name="assembly"></param>
        /// <param name="target"></param>
        /// <source>http://stackoverflow.com/a/1600990</source>
        /// <returns></returns>
        public static DateTime GetLinkerTime(this Assembly assembly, TimeZoneInfo target = null)
        {
            var filePath = assembly.Location;
            const int c_PeHeaderOffset = 60;
            const int c_LinkerTimestampOffset = 8;

            var buffer = new byte[2048];

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                stream.Read(buffer, 0, 2048);
            }

            var offset = BitConverter.ToInt32(buffer, c_PeHeaderOffset);
            var secondsSince1970 = BitConverter.ToInt32(buffer, offset + c_LinkerTimestampOffset);
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var linkTimeUtc = epoch.AddSeconds(secondsSince1970);

            var tz = target ?? TimeZoneInfo.Local;
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(linkTimeUtc, tz);

            return localTime;
        }


        /// <summary>
        /// Checks if a form of a given type is already open in application
        /// </summary>
        /// <param name="form"></param>
        /// <returns></returns>
        public static bool IsFormOpen(Type form)
        {
            FormCollection openForms = Application.OpenForms;

            foreach (var f in openForms)
            {
                if (f.GetType() == form)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Generates a filename for a given path, if the a file does not already exist at the filepath
        /// then that path is used. If one does exist then a number is added at the end of the file name ('testfile(1)')
        /// and incremented until a path that doesn't already exist is found
        /// </summary>
        /// <param name="filepath"></param>
        /// <returns></returns>
        public static string GenerateFilename(string filepath)
        {
            string filename = Path.GetFileNameWithoutExtension(filepath);
            string extension = Path.GetExtension(filepath);
            string directory = Path.GetDirectoryName(filepath);

            string currentFilePath = filepath;
            bool goodFilename = false;
            int counter = 0;

            // Loop through possible names till we find one that doesn't already exist
            while (goodFilename == false)
            {

                if (File.Exists(currentFilePath))
                {
                    counter++;
                    currentFilePath = Path.Combine(directory, $"{filename}({counter}){extension}");
                }
                else
                {
                    goodFilename = true;
                }
            }

            return currentFilePath;
        }

        public static double ConvertBytesToMegabytes(long bytes)
        {
            return (bytes / 1024f) / 1024f;
        }
    }
}