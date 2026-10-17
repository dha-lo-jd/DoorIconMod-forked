using KMod;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DoorIcons
{
    public static class Pathfinder
    {
        public static bool FindModRootPath(string modName, string workshopID, out string? rootPath)
        {
            string modsPath = Manager.GetDirectory().TrimEnd('/', '\\');
            string? firstRootPath = null;

            var possibleLocations = new string[]
            {
                MergePath(modsPath, "Steam"),
                MergePath(modsPath, "Local"),
                MergePath(modsPath, "Dev"),
                "Mods"
            };

            var possibleNames = new string[]
            {
                workshopID,
                modName
            };

            foreach (var location in possibleLocations)
            {
                if (!Directory.Exists(location))
                {
                    continue;
                }

                foreach (var name in possibleNames)
                {
                    string[] directories = Directory.GetDirectories(
                        location,
                        name,
                        SearchOption.TopDirectoryOnly
                    );

                    foreach (var directory in directories)
                    {
                        firstRootPath ??= directory;
                        var portraitPath = MergePath(directory, "Sprites", "portrait_standard.png");
                        if (File.Exists(portraitPath))
                        {
                            rootPath = directory;
                            return true;
                        }
                    }
                }
            }

            if (firstRootPath != null)
            {
                rootPath = firstRootPath;
                return true;
            }

            rootPath = null;
            return false;
        }

        public static string MergePath(params string[] pathSegments)
        {
            var builder = new StringBuilder(pathSegments[0]);

            for (int i = 1; i < pathSegments.Length; i++)
            {
                builder.Append(Path.DirectorySeparatorChar + pathSegments[i]);
            }

            return builder.ToString();
        }
    }
}