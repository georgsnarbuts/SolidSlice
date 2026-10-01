// SolidWorks add-in: "3D Print" tab
//  - Export STL: saves the active part/assembly as STL next to the file, no dialogs.
//  - One button per installed slicer (Bambu Studio, OrcaSlicer, PrusaSlicer, Cura, ...):
//    exports to a temp STL and opens it in that slicer, reusing a running window.
// Note: compiled with the .NET Framework csc.exe (C# 5) - no string interpolation / ?. operators.

using System;
using System.Collections.Generic;
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

namespace SolidSlice
{
    [ComVisible(true)]
    [Guid("2c12f6d8-0a49-49a8-abaf-ef8bd194d6a2")]
    [ProgId("SolidSlice.Addin")]
    public class SolidSliceAddin : ISwAddin
    {
        const int CmdGroupId = 0xB4B0;
        const string TabName = "3D Print";
        const string LegacyTabName = "Bambu";  // tab name used by v1, removed on load
        const int ExportFlyoutId = 0xB4B1;
        const string SettingsKey = @"Software\SolidSlice";
        const string ExportHint = "Save the active document as STL next to its file";
        const string ExportAsHint = "Choose where to save the STL";
        static readonly int[] IconSizes = { 20, 32, 40, 64, 96, 128 };
        static readonly Color StlColor = Color.FromArgb(70, 80, 95);

        SldWorks swApp;
        ICommandManager cmdMgr;
        int addinCookie;
        // slicers found on this PC, index = callback argument
        readonly List<Slicer> slicers = new List<Slicer>();
        readonly List<string> slicerExes = new List<string>();

        #region ISwAddin

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            swApp = (SldWorks)ThisSW;
            addinCookie = Cookie;
            swApp.SetAddinCallbackInfo2(0, this, addinCookie);
            cmdMgr = swApp.GetCommandManager(addinCookie);

            foreach (Slicer s in Slicer.All)
            {
                string exe = null;
                try { exe = s.FindExe(); } catch { }
                if (exe == null) continue;
                slicers.Add(s);
                slicerExes.Add(exe);
            }

            AddCommands();
            return true;
        }

        public bool DisconnectFromSW()
        {
            try { cmdMgr.RemoveCommandGroup2(CmdGroupId, true); } catch { }
            try { cmdMgr.RemoveFlyoutGroup(ExportFlyoutId); } catch { }
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
            // icon strip: [Export STL][Export STL As][slicer 0][slicer 1]...
            var iconColors = new List<Color> { StlColor, StlColor };
            var iconLetters = new List<string> { null, null };
            foreach (Slicer s in slicers) { iconColors.Add(s.Color); iconLetters.Add(s.Letter); }
            string[] icons = BuildIconStrips(iconColors, iconLetters);
            string[] mainIcons = BuildIconStrips(new List<Color> { StlColor }, new List<string> { null });

            int err = 0;
            CommandGroup grp = cmdMgr.CreateCommandGroup2(CmdGroupId, TabName,
                "Export STL and send to slicers", "3D Print", -1, true, ref err);
            grp.IconList = icons;
            grp.MainIconList = mainIcons;

            const int itemType = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
            var items = new List<int>();
            // menu entries; on the tab both live in the Export STL flyout below
            grp.AddCommandItem2("Export STL", -1, ExportHint, "Export STL",
                0, "ExportStl", "CanExport", 0, (int)swCommandItemType_e.swMenuItem);
            grp.AddCommandItem2("Export STL As...", -1, ExportAsHint, "Export STL As",
                1, "ExportStlAs", "CanExport", 1, (int)swCommandItemType_e.swMenuItem);
            for (int i = 0; i < slicers.Count; i++)
            {
                string hint = "Send to " + slicers[i].Name;
                items.Add(grp.AddCommandItem2(slicers[i].Name, -1,
                    "Export the active document and open it in " + slicers[i].Name, hint,
                    i + 2, "SendToSlicer(" + i + ")", "CanExport", i + 2, itemType));
            }

            grp.HasToolbar = true;
            grp.HasMenu = true;
            grp.Activate();

            var ids = new List<int>();
            foreach (int idx in items) ids.Add(grp.get_CommandID(idx));

            // Export STL dropdown: "Save next to part" / "Choose location...".
            // Same pattern as the SolidWorks add-in template: Simple style, items (re)added in the
            // open callback. (Favorite style raised "An invalid argument was encountered".)
            try { cmdMgr.RemoveFlyoutGroup(ExportFlyoutId); } catch { }
            string[] stlIcons = BuildIconStrips(new List<Color> { StlColor, StlColor }, new List<string> { null, null });
            FlyoutGroup fly = cmdMgr.CreateFlyoutGroup2(ExportFlyoutId, "Export STL", "Export STL", ExportHint,
                mainIcons, stlIcons, "ExportFlyoutOpened", "ExportFlyoutEnable");
            AddExportFlyoutItems(fly);
            fly.FlyoutType = (int)swCommandFlyoutStyle_e.swCommandFlyoutStyle_Simple;

            foreach (int docType in new[] { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY })
            {
                foreach (string name in new[] { TabName, LegacyTabName })
                {
                    CommandTab old = cmdMgr.GetCommandTab(docType, name);
                    if (old != null) cmdMgr.RemoveCommandTab(old);
                }
                CommandTab tab = cmdMgr.AddCommandTab(docType, TabName);
                const int textBelow = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow;

                // [Export STL ▾] | [slicers...]
                tab.AddCommandTabBox().AddCommands(new[] { fly.CmdID },
                    new[] { textBelow | (int)swCommandTabButtonFlyoutStyle_e.swCommandTabButton_SimpleFlyout });
                if (ids.Count > 0)
                {
                    int[] styles = new int[ids.Count];
                    for (int i = 0; i < styles.Length; i++) styles[i] = textBelow;
                    tab.AddCommandTabBox().AddCommands(ids.ToArray(), styles);
                }
            }
        }

        // One PNG strip per size with an icon per command, as SolidWorks expects.
        // color+letter = slicer icon (colored circle with letter), null letter = STL icon (arrow into tray).
        static string[] BuildIconStrips(List<Color> colors, List<string> letters)
        {
            string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SolidSlice", "icons");
            Directory.CreateDirectory(dir);
            string tag = "v2_" + string.Join("", letters.ConvertAll(l => l ?? "_").ToArray());
            var paths = new string[IconSizes.Length];
            for (int i = 0; i < IconSizes.Length; i++)
            {
                int s = IconSizes[i];
                string p = Path.Combine(dir, tag + "_" + s + ".png");
                paths[i] = p;
                if (File.Exists(p)) continue;
                using (var bmp = new Bitmap(s * colors.Count, s, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    for (int n = 0; n < colors.Count; n++)
                    {
                        g.ResetTransform();
                        g.TranslateTransform(n * s, 0);
                        if (letters[n] == null) DrawStlIcon(g, s, colors[n]);
                        else DrawLetterIcon(g, s, colors[n], letters[n]);
                    }
                    bmp.Save(p, ImageFormat.Png);
                }
            }
            return paths;
        }

        static void DrawLetterIcon(Graphics g, int s, Color color, string letter)
        {
            float m = s * 0.06f;
            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, m, m, s - 2 * m, s - 2 * m);
            float fontPx = s * (letter.Length > 1 ? 0.36f : 0.5f);
            using (var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(letter, font, Brushes.White, new RectangleF(0, s * 0.02f, s, s), fmt);
        }

        static void DrawStlIcon(Graphics g, int s, Color color)
        {
            using (var brush = new SolidBrush(color))
            {
                // down arrow
                float c = s / 2f;
                g.FillPolygon(brush, new[] {
                    new PointF(c - s * 0.1f, s * 0.08f), new PointF(c + s * 0.1f, s * 0.08f),
                    new PointF(c + s * 0.1f, s * 0.38f), new PointF(c + s * 0.26f, s * 0.38f),
                    new PointF(c, s * 0.64f), new PointF(c - s * 0.26f, s * 0.38f),
                    new PointF(c - s * 0.1f, s * 0.38f) });
                // tray
                float w = Math.Max(1.5f, s * 0.1f);
                using (var pen = new Pen(brush, w) { LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[] {
                        new PointF(s * 0.12f, s * 0.6f), new PointF(s * 0.12f, s * 0.88f),
                        new PointF(s * 0.88f, s * 0.88f), new PointF(s * 0.88f, s * 0.6f) });
            }
        }

        #endregion

        #region Commands (called by SolidWorks via callback names)

        public int CanExport()
        {
            var doc = swApp.ActiveDoc as ModelDoc2;
            if (doc == null) return 0;
            int t = doc.GetType();
            return (t == (int)swDocumentTypes_e.swDocPART || t == (int)swDocumentTypes_e.swDocASSEMBLY) ? 1 : 0;
        }

        // Export STL: next to the part file (asks for a location if the part was never saved).
        public void ExportStl()
        {
            Export(false);
        }

        // Export STL As...: always asks for a location.
        public void ExportStlAs()
        {
            Export(true);
        }

        // Called when the Export STL flyout opens.
        public void ExportFlyoutOpened()
        {
            FlyoutGroup fly = cmdMgr.GetFlyoutGroup(ExportFlyoutId);
            if (fly == null) return;
            fly.RemoveAllCommandItems();
            AddExportFlyoutItems(fly);
        }

        public int ExportFlyoutEnable()
        {
            return 1;
        }

        static void AddExportFlyoutItems(FlyoutGroup fly)
        {
            fly.AddCommandItem("Save next to part", ExportHint, 0, "ExportStl", "CanExport");
            fly.AddCommandItem("Choose location...", ExportAsHint, 1, "ExportStlAs", "CanExport");
        }

        void Export(bool chooseLocation)
        {
            try
            {
                var doc = swApp.ActiveDoc as ModelDoc2;
                if (doc == null) return;

                string partDir = Path.GetDirectoryName(doc.GetPathName());
                string outPath;
                if (chooseLocation || string.IsNullOrEmpty(partDir))
                {
                    outPath = AskStlPath(BaseName(doc) + ".stl", partDir);
                    if (outPath == null) return;
                }
                else outPath = Path.Combine(partDir, BaseName(doc) + ".stl");

                if (SaveStl(doc, outPath)) Status("Exported " + outPath);
            }
            catch (Exception ex)
            {
                Warn("STL export failed:\n" + ex.Message);
            }
        }

        // Save dialog that starts in the last used export folder (or the part's folder).
        string AskStlPath(string fileName, string partDir)
        {
            string lastDir;
            using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                lastDir = k == null ? null : k.GetValue("LastExportFolder") as string;

            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Export STL";
                dlg.Filter = "STL (*.stl)|*.stl";
                dlg.FileName = fileName;
                if (!string.IsNullOrEmpty(lastDir) && Directory.Exists(lastDir)) dlg.InitialDirectory = lastDir;
                else if (!string.IsNullOrEmpty(partDir)) dlg.InitialDirectory = partDir;

                if (dlg.ShowDialog(new SwWindow(swApp)) != DialogResult.OK) return null;

                using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    k.SetValue("LastExportFolder", Path.GetDirectoryName(dlg.FileName));
                return dlg.FileName;
            }
        }

        // Lets WinForms dialogs use the SolidWorks main window as their owner.
        class SwWindow : IWin32Window
        {
            readonly IntPtr handle;
            public SwWindow(SldWorks app) { handle = new IntPtr(((Frame)app.Frame()).GetHWndx64()); }
            public IntPtr Handle { get { return handle; } }
        }

        public void SendToSlicer(string index)
        {
            try
            {
                int i = int.Parse(index);
                var doc = swApp.ActiveDoc as ModelDoc2;
                if (doc == null) return;

                string exe = slicerExes[i];
                if (!File.Exists(exe)) { Warn(slicers[i].Name + " was not found at:\n" + exe); return; }

                string dir = Path.Combine(Path.GetTempPath(), "SolidSlice");
                Directory.CreateDirectory(dir);
                string stl = Path.Combine(dir, BaseName(doc) + ".stl");
                if (!SaveStl(doc, stl)) return;

                var psi = new ProcessStartInfo(exe, string.Format(slicers[i].ArgsFormat, stl));
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                Process.Start(psi);
                Status("Sent " + Path.GetFileName(stl) + " to " + slicers[i].Name);
            }
            catch (Exception ex)
            {
                Warn("Send to slicer failed:\n" + ex.Message);
            }
        }

        #endregion

        #region Export

        // File name without extension, plus the configuration name if it isn't "Default".
        static string BaseName(ModelDoc2 doc)
        {
            string name = Path.GetFileNameWithoutExtension(doc.GetPathName());
            if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(doc.GetTitle());
            string config = doc.ConfigurationManager.ActiveConfiguration.Name;
            if (!string.IsNullOrEmpty(config) && config != "Default") name += " - " + config;
            foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return name;
        }

        // Saves a binary, millimetre, single-file STL copy; the user's STL settings are restored afterwards.
        bool SaveStl(ModelDoc2 doc, string outPath)
        {
            if (File.Exists(outPath)) File.Delete(outPath);

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
                return false;
            }
            return true;
        }

        #endregion

        void Warn(string msg)
        {
            swApp.SendMsgToUser2(msg, (int)swMessageBoxIcon_e.swMbWarning, (int)swMessageBoxBtn_e.swMbOk);
        }

        void Status(string msg)
        {
            var frame = swApp.Frame() as Frame;
            if (frame != null) frame.SetStatusBarText(msg);
        }

        #region COM registration

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            string guid = "{" + t.GUID.ToString() + "}";
            using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + guid))
            {
                k.SetValue(null, 1);
                k.SetValue("Title", "SolidSlice (3D Print: Export STL / Send to Slicer)");
                k.SetValue("Description", "One-click STL export and send to Bambu Studio, OrcaSlicer, PrusaSlicer, Cura");
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
