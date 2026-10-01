// Known slicers and how to find them on this PC.
// To add a slicer, add an entry to Slicer.All.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Microsoft.Win32;

namespace SwToBambu
{
    public class Slicer
    {
        public string Name;              // button label, also the registry override value name
        public string[] DisplayNames;    // prefixes of the entry in "Installed apps"
        public string[] ExeNames;        // executable file names
        public string[] DefaultDirs;     // folders under Program Files to check (may contain * wildcards)
        public string ArgsFormat;        // {0} = model file path
        public Color Color;              // icon color
        public string Letter;            // icon letter

        // Slicers are started with just the file path. Whether that reuses an open window is
        // decided by the slicer's own "single instance" preference (see README). Don't pass
        // --single-instance: Bambu Studio rejects it and exits with code -2.
        public static readonly Slicer[] All =
        {
            new Slicer {
                Name = "Bambu Studio", DisplayNames = new[] { "Bambu Studio" },
                ExeNames = new[] { "bambu-studio.exe" }, DefaultDirs = new[] { "Bambu Studio" },
                ArgsFormat = "\"{0}\"", Color = Color.FromArgb(0, 174, 66), Letter = "B" },
            new Slicer {
                Name = "OrcaSlicer", DisplayNames = new[] { "OrcaSlicer", "Orca Slicer" },
                ExeNames = new[] { "orca-slicer.exe" }, DefaultDirs = new[] { "OrcaSlicer" },
                ArgsFormat = "\"{0}\"", Color = Color.FromArgb(0, 150, 136), Letter = "O" },
            new Slicer {
                Name = "PrusaSlicer", DisplayNames = new[] { "PrusaSlicer" },
                ExeNames = new[] { "prusa-slicer.exe" }, DefaultDirs = new[] { @"Prusa3D\PrusaSlicer" },
                ArgsFormat = "\"{0}\"", Color = Color.FromArgb(250, 104, 49), Letter = "P" },
            new Slicer {
                Name = "Cura", DisplayNames = new[] { "UltiMaker Cura", "Ultimaker Cura" },
                ExeNames = new[] { "UltiMaker-Cura.exe", "Ultimaker-Cura.exe", "Cura.exe" },
                DefaultDirs = new[] { "UltiMaker Cura*", "Ultimaker Cura*" },
                ArgsFormat = "\"{0}\"", Color = Color.FromArgb(25, 110, 240), Letter = "C" },
            new Slicer {
                Name = "Creality Print", DisplayNames = new[] { "Creality Print", "CrealityPrint" },
                ExeNames = new[] { "CrealityPrint.exe", "Creality Print.exe" },
                DefaultDirs = new[] { "Creality\\Creality Print*", "Creality Print*" },
                ArgsFormat = "\"{0}\"", Color = Color.FromArgb(40, 40, 40), Letter = "CP" },
        };

        const string OverrideKey = @"Software\SwToBambu\Slicers";

        // Returns the slicer's executable, or null if it isn't installed.
        public string FindExe()
        {
            // 1. manual override: HKCU\Software\SwToBambu\Slicers, value <Name> = full exe path
            using (var k = Registry.CurrentUser.OpenSubKey(OverrideKey))
            {
                string p = k == null ? null : k.GetValue(Name) as string;
                if (!string.IsNullOrEmpty(p)) return File.Exists(p) ? p : null;
            }

            // 2. "Installed apps" (uninstall) entries
            foreach (var app in InstalledApps())
            {
                if (!MatchesDisplayName(app.Key)) continue;
                string icon = app.Value[0], location = app.Value[1];
                if (!string.IsNullOrEmpty(icon))
                {
                    icon = icon.Split(',')[0].Trim('"');
                    if (IsOurExe(icon) && File.Exists(icon)) return icon;
                    if (string.IsNullOrEmpty(location)) location = Path.GetDirectoryName(icon);
                }
                string inDir = FindExeIn(location);
                if (inDir != null) return inDir;
            }

            // 3. default install folders
            foreach (string root in new[] {
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86) })
                foreach (string pattern in DefaultDirs)
                {
                    string parent = Path.Combine(root, Path.GetDirectoryName(pattern) ?? "");
                    if (!Directory.Exists(parent)) continue;
                    string[] dirs = Directory.GetDirectories(parent, Path.GetFileName(pattern));
                    Array.Sort(dirs);
                    Array.Reverse(dirs);  // newest version folder first
                    foreach (string d in dirs)
                    {
                        string exe = FindExeIn(d);
                        if (exe != null) return exe;
                    }
                }
            return null;
        }

        bool MatchesDisplayName(string displayName)
        {
            foreach (string prefix in DisplayNames)
                if (displayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        bool IsOurExe(string path)
        {
            foreach (string exe in ExeNames)
                if (string.Equals(Path.GetFileName(path), exe, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        string FindExeIn(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            foreach (string exe in ExeNames)
            {
                string p = Path.Combine(dir, exe);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        // DisplayName -> [DisplayIcon, InstallLocation] for every installed program
        static List<KeyValuePair<string, string[]>> InstalledApps()
        {
            var result = new List<KeyValuePair<string, string[]>>();
            string[] roots = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (string root in roots)
                    using (var k = hive.OpenSubKey(root))
                    {
                        if (k == null) continue;
                        foreach (string sub in k.GetSubKeyNames())
                            using (var app = k.OpenSubKey(sub))
                            {
                                if (app == null) continue;
                                string dn = app.GetValue("DisplayName") as string;
                                if (string.IsNullOrEmpty(dn)) continue;
                                result.Add(new KeyValuePair<string, string[]>(dn, new[] {
                                    app.GetValue("DisplayIcon") as string,
                                    app.GetValue("InstallLocation") as string }));
                            }
                    }
            return result;
        }
    }
}
