// SolidWorks add-in: "Send to Bambu Studio"
// Exports the active part/assembly to STL (mm, binary) and opens it in Bambu Studio.
// Adds a "3D Print" CommandManager tab and menu in parts and assemblies.
// Note: compiled with the .NET Framework csc.exe (C# 5) - no string interpolation / ?. operators.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace SwToBambu
{
    [ComVisible(true)]
    [Guid("2c12f6d8-0a49-49a8-abaf-ef8bd194d6a2")]
    [ProgId("SwToBambu.Addin")]
    public class SwToBambuAddin : ISwAddin
    {
        const int CmdGroupId = 0xB4B0;
        const string TabName = "3D Print";
        const string LegacyTabName = "Bambu";  // tab name used by v1, removed on load
        const string SettingsKey = @"Software\SwToBambu";
        static readonly int[] IconSizes = { 20, 32, 40, 64, 96, 128 };

        SldWorks swApp;
        ICommandManager cmdMgr;
        int addinCookie;

        #region ISwAddin

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            swApp = (SldWorks)ThisSW;
            addinCookie = Cookie;
            swApp.SetAddinCallbackInfo2(0, this, addinCookie);
            cmdMgr = swApp.GetCommandManager(addinCookie);
            AddCommands();
            return true;
        }

        public bool DisconnectFromSW()
        {
            try { cmdMgr.RemoveCommandGroup2(CmdGroupId, true); } catch { }
            Marshal.ReleaseComObject(cmdMgr);
            cmdMgr = null;
            swApp = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        #endregion

        #region UI

        void AddCommands()
        {
            string[] icons = BuildIcons();
            int err = 0;

            CommandGroup grp = cmdMgr.CreateCommandGroup2(CmdGroupId, TabName,
                "Send to Bambu Studio", "Send to Bambu Studio", -1, true, ref err);
            grp.IconList = icons;
            grp.MainIconList = icons;

            int itemIdx = grp.AddCommandItem2("Send to Bambu Studio", -1,
                "Export the active part to STL and open it in Bambu Studio",
                "Send to Bambu Studio", 0, "SendToBambu", "SendToBambuEnable", 0,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

            grp.HasToolbar = true;
            grp.HasMenu = true;
            grp.Activate();

            int cmdId = grp.get_CommandID(itemIdx);
            foreach (int docType in new[] { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY })
            {
                foreach (string name in new[] { TabName, LegacyTabName })
                {
                    CommandTab old = cmdMgr.GetCommandTab(docType, name);
                    if (old != null) cmdMgr.RemoveCommandTab(old);
                }
                CommandTab tab = cmdMgr.AddCommandTab(docType, TabName);
                CommandTabBox box = tab.AddCommandTabBox();
                box.AddCommands(new[] { cmdId },
                    new[] { (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow });
            }
        }

        // Draws simple green "send" icons at the sizes SolidWorks asks for.
        static string[] BuildIcons()
        {
            string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SwToBambu", "icons");
            Directory.CreateDirectory(dir);
            var paths = new string[IconSizes.Length];
            for (int i = 0; i < IconSizes.Length; i++)
            {
                int s = IconSizes[i];
                string p = Path.Combine(dir, "icon_" + s + ".png");
                paths[i] = p;
                if (File.Exists(p)) continue;
                using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float m = s * 0.06f;
                    using (var brush = new SolidBrush(Color.FromArgb(0, 174, 66)))
                        g.FillEllipse(brush, m, m, s - 2 * m, s - 2 * m);
                    // white arrow pointing right
                    float c = s / 2f;
                    var arrow = new[]
                    {
                        new PointF(s * 0.25f, c - s * 0.09f), new PointF(c, c - s * 0.09f),
                        new PointF(c, s * 0.24f), new PointF(s * 0.78f, c),
                        new PointF(c, s * 0.76f), new PointF(c, c + s * 0.09f),
                        new PointF(s * 0.25f, c + s * 0.09f)
                    };
                    g.FillPolygon(Brushes.White, arrow);
                    bmp.Save(p, ImageFormat.Png);
                }
            }
            return paths;
        }

        #endregion

        #region Commands (called by SolidWorks via callback names)

        public int SendToBambuEnable()
        {
            var doc = swApp.ActiveDoc as ModelDoc2;
            if (doc == null) return 0;
            int t = doc.GetType();
            return (t == (int)swDocumentTypes_e.swDocPART || t == (int)swDocumentTypes_e.swDocASSEMBLY) ? 1 : 0;
        }

        public void SendToBambu()
        {
            try
            {
                var doc = swApp.ActiveDoc as ModelDoc2;
                if (doc == null) { Warn("No active document."); return; }

                string exe = FindBambuStudio();
                if (exe == null) return;

                string stl = ExportStl(doc);
                if (stl == null) return;

                // --single-instance hands the file to an already running Bambu Studio
                // (if any) instead of opening a second window.
                var psi = new ProcessStartInfo(exe, "--single-instance \"" + stl + "\"");
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Warn("Send to Bambu Studio failed:\n" + ex.Message);
            }
        }

        string ExportStl(ModelDoc2 doc)
        {
            string name = Path.GetFileNameWithoutExtension(doc.GetPathName());
            if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(doc.GetTitle());
            string config = doc.ConfigurationManager.ActiveConfiguration.Name;
            if (!string.IsNullOrEmpty(config) && config != "Default") name += " - " + config;
            foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');

            string dir = Path.Combine(Path.GetTempPath(), "SwToBambu");
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, name + ".stl");
            if (File.Exists(outPath)) File.Delete(outPath);

            // Force mm / binary / single file, then restore the user's settings.
            var unitsPref = swUserPreferenceIntegerValue_e.swExportStlUnits;
            var binPref = swUserPreferenceToggle_e.swSTLBinaryFormat;
            var onePref = swUserPreferenceToggle_e.swSTLComponentsIntoOneFile;

            int oldUnits = swApp.GetUserPreferenceIntegerValue((int)unitsPref);
            bool oldBin = swApp.GetUserPreferenceToggle((int)binPref);
            bool oldOne = swApp.GetUserPreferenceToggle((int)onePref);

            int errs = 0, warns = 0;
            bool ok;
            try
            {
                swApp.SetUserPreferenceIntegerValue((int)unitsPref, (int)swLengthUnit_e.swMM);
                swApp.SetUserPreferenceToggle((int)binPref, true);
                swApp.SetUserPreferenceToggle((int)onePref, true);

                ok = doc.Extension.SaveAs(outPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy),
                    null, ref errs, ref warns);
            }
            finally
            {
                swApp.SetUserPreferenceIntegerValue((int)unitsPref, oldUnits);
                swApp.SetUserPreferenceToggle((int)binPref, oldBin);
                swApp.SetUserPreferenceToggle((int)onePref, oldOne);
            }

            if (!ok || !File.Exists(outPath))
            {
                Warn("STL export failed (error code " + errs + ").");
                return null;
            }
            return outPath;
        }

        #endregion

        #region Bambu Studio lookup

        string FindBambuStudio()
        {
            // 1. path remembered from a previous browse
            using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                string p = k == null ? null : k.GetValue("BambuStudioPath") as string;
                if (p != null && File.Exists(p)) return p;
            }

            // 2. installer registry entries
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
                                string dn = app.GetValue("DisplayName") as string;
                                if (dn == null || !dn.StartsWith("Bambu Studio", StringComparison.OrdinalIgnoreCase)) continue;
                                string icon = app.GetValue("DisplayIcon") as string;
                                if (icon != null)
                                {
                                    icon = icon.Split(',')[0].Trim('"');
                                    if (File.Exists(icon)) return icon;
                                }
                            }
                    }

            // 3. default location
            string def = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "Bambu Studio", "bambu-studio.exe");
            if (File.Exists(def)) return def;

            // 4. ask the user once
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Locate bambu-studio.exe";
                dlg.Filter = "Bambu Studio|bambu-studio.exe|Programs (*.exe)|*.exe";
                if (dlg.ShowDialog() != DialogResult.OK) return null;
                using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    k.SetValue("BambuStudioPath", dlg.FileName);
                return dlg.FileName;
            }
        }

        #endregion

        void Warn(string msg)
        {
            swApp.SendMsgToUser2(msg, (int)swMessageBoxIcon_e.swMbWarning, (int)swMessageBoxBtn_e.swMbOk);
        }

        #region COM registration

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            string guid = "{" + t.GUID.ToString() + "}";
            using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + guid))
            {
                k.SetValue(null, 1);
                k.SetValue("Title", "Send to Bambu Studio");
                k.SetValue("Description", "Exports the active part to STL and opens it in Bambu Studio");
            }
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + guid))
                k.SetValue(null, 1);
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            string guid = "{" + t.GUID.ToString() + "}";
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\SolidWorks\Addins\" + guid, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\SolidWorks\AddInsStartup\" + guid, false);
        }

        #endregion
    }
}
