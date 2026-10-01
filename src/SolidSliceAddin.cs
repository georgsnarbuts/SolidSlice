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
        const int QualityFlyoutId = 0xB4B2;
        const string QualityIcon = "#Q";  // icon-strip marker: draw the triangle-mesh icon
        static readonly int[] IconSizes = { 20, 32, 40, 64, 96, 128 };
        static readonly Color StlColor = Color.FromArgb(70, 80, 95);

        // STL resolution presets used by every export (Export STL and all slicer buttons).
        // Index 0 keeps the user's own SolidWorks STL options.
        class StlQuality
        {
            public string Name; public double DeviationMm; public double AngleDeg;
            public StlQuality(string name, double deviationMm, double angleDeg)
            { Name = name; DeviationMm = deviationMm; AngleDeg = angleDeg; }
            public string Label
            {
                get { return DeviationMm <= 0 ? Name : Name + "  (" + DeviationMm + " mm, " + AngleDeg + "°)"; }
            }
        }
        static readonly StlQuality[] Qualities =
        {
            new StlQuality("SolidWorks setting", 0, 0),
            new StlQuality("Draft", 0.1, 20),
            new StlQuality("Normal", 0.03, 10),
            new StlQuality("High", 0.01, 5),
            new StlQuality("Ultra", 0.002, 2),
        };

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
            try { cmdMgr.RemoveFlyoutGroup(QualityFlyoutId); } catch { }
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

            // Quality dropdown: global STL resolution preset, current one marked with a check
            try { cmdMgr.RemoveFlyoutGroup(QualityFlyoutId); } catch { }
            var qColors = new List<Color>();
            var qLetters = new List<string>();
            foreach (StlQuality q in Qualities) { qColors.Add(StlColor); qLetters.Add(QualityIcon); }
            string[] qMainIcons = BuildIconStrips(new List<Color> { StlColor }, new List<string> { QualityIcon });
            FlyoutGroup qFly = cmdMgr.CreateFlyoutGroup2(QualityFlyoutId, "Quality", "STL quality",
                "Triangle resolution used by Export STL and all slicer buttons",
                qMainIcons, BuildIconStrips(qColors, qLetters), "QualityFlyoutOpened", "AlwaysEnabled");
            AddQualityFlyoutItems(qFly);
            qFly.FlyoutType = (int)swCommandFlyoutStyle_e.swCommandFlyoutStyle_Simple;

            foreach (int docType in new[] { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY })
            {
                foreach (string name in new[] { TabName, LegacyTabName })
                {
                    CommandTab old = cmdMgr.GetCommandTab(docType, name);
                    if (old != null) cmdMgr.RemoveCommandTab(old);
                }
                CommandTab tab = cmdMgr.AddCommandTab(docType, TabName);
                const int textBelow = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow;

                // [Export STL ▾] [Quality ▾] | [slicers...]
                const int flyoutStyle = textBelow | (int)swCommandTabButtonFlyoutStyle_e.swCommandTabButton_SimpleFlyout;
                tab.AddCommandTabBox().AddCommands(new[] { fly.CmdID, qFly.CmdID }, new[] { flyoutStyle, flyoutStyle });
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
                        else if (letters[n] == QualityIcon) DrawQualityIcon(g, s, colors[n]);
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

        // Triangle split into four smaller triangles (a mesh).
        static void DrawQualityIcon(Graphics g, int s, Color color)
        {
            var a = new PointF(s * 0.5f, s * 0.1f);
            var b = new PointF(s * 0.08f, s * 0.86f);
            var c = new PointF(s * 0.92f, s * 0.86f);
            var ab = new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var bc = new PointF((b.X + c.X) / 2, (b.Y + c.Y) / 2);
            var ca = new PointF((c.X + a.X) / 2, (c.Y + a.Y) / 2);
            using (var fill = new SolidBrush(Color.FromArgb(60, color)))
                g.FillPolygon(fill, new[] { a, b, c });
            using (var pen = new Pen(color, Math.Max(1.2f, s * 0.07f)) { LineJoin = LineJoin.Round })
            {
                g.DrawPolygon(pen, new[] { a, b, c });
                g.DrawPolygon(pen, new[] { ab, bc, ca });
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

        public int AlwaysEnabled()
        {
            return 1;
        }

        // Quality presets. One method per preset: flyout items get plain callback names.
        public void SetQuality0() { SetQuality(0); }
        public void SetQuality1() { SetQuality(1); }
        public void SetQuality2() { SetQuality(2); }
        public void SetQuality3() { SetQuality(3); }
        public void SetQuality4() { SetQuality(4); }

        void SetQuality(int index)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                k.SetValue("StlQuality", index);
            Status("STL quality: " + Qualities[index].Label);
        }

        static int CurrentQuality()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                object v = k == null ? null : k.GetValue("StlQuality");
                int i = v is int ? (int)v : 0;
                return i >= 0 && i < Qualities.Length ? i : 0;
            }
        }

        // Called when the Quality flyout opens: rebuild so the check mark follows the current preset.
        public void QualityFlyoutOpened()
        {
            FlyoutGroup fly = cmdMgr.GetFlyoutGroup(QualityFlyoutId);
            if (fly == null) return;
            fly.RemoveAllCommandItems();
            AddQualityFlyoutItems(fly);
        }

        static void AddQualityFlyoutItems(FlyoutGroup fly)
        {
            int current = CurrentQuality();
            for (int i = 0; i < Qualities.Length; i++)
            {
                string hint = i == 0 ? "Use the STL options from File > Save As > STL > Options"
                    : "Max deviation " + Qualities[i].DeviationMm + " mm, max angle " + Qualities[i].AngleDeg + "°";
                fly.AddCommandItem((i == current ? "✓ " : "     ") + Qualities[i].Label, hint, i,
                    "SetQuality" + i, "AlwaysEnabled");
            }
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

            var qualityPref = swUserPreferenceIntegerValue_e.swSTLQuality;
            var devPref = swUserPreferenceDoubleValue_e.swSTLDeviation;      // metres
            var anglePref = swUserPreferenceDoubleValue_e.swSTLAngleTolerance;  // radians

            int oldUnits = swApp.GetUserPreferenceIntegerValue((int)unitsPref);
            bool oldBin = swApp.GetUserPreferenceToggle((int)binPref);
            bool oldOne = swApp.GetUserPreferenceToggle((int)onePref);
            int oldQuality = swApp.GetUserPreferenceIntegerValue((int)qualityPref);
            double oldDev = swApp.GetUserPreferenceDoubleValue((int)devPref);
            double oldAngle = swApp.GetUserPreferenceDoubleValue((int)anglePref);
            StlQuality quality = Qualities[CurrentQuality()];

            int errs = 0, warns = 0;
            bool ok;
            try
            {
                swApp.SetUserPreferenceIntegerValue((int)unitsPref, (int)swLengthUnit_e.swMM);
                swApp.SetUserPreferenceToggle((int)binPref, true);
                swApp.SetUserPreferenceToggle((int)onePref, true);
                if (quality.DeviationMm > 0)
                {
                    swApp.SetUserPreferenceIntegerValue((int)qualityPref, (int)swSTLQuality_e.swSTLQuality_Custom);
                    swApp.SetUserPreferenceDoubleValue((int)devPref, quality.DeviationMm / 1000.0);
                    swApp.SetUserPreferenceDoubleValue((int)anglePref, quality.AngleDeg * Math.PI / 180.0);
                }

                ok = doc.Extension.SaveAs(outPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy),
                    null, ref errs, ref warns);
            }
            finally
            {
                swApp.SetUserPreferenceIntegerValue((int)unitsPref, oldUnits);
                swApp.SetUserPreferenceToggle((int)binPref, oldBin);
                swApp.SetUserPreferenceToggle((int)onePref, oldOne);
                if (quality.DeviationMm > 0)
                {
                    swApp.SetUserPreferenceIntegerValue((int)qualityPref, oldQuality);
                    swApp.SetUserPreferenceDoubleValue((int)devPref, oldDev);
                    swApp.SetUserPreferenceDoubleValue((int)anglePref, oldAngle);
                }
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
