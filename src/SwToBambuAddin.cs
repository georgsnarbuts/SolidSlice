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
            // icon strip: [Export STL][slicer 0][slicer 1]...
            var iconColors = new List<Color> { StlColor };
            var iconLetters = new List<string> { null };
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
            items.Add(grp.AddCommandItem2("Export STL", -1,
                "Save the active document as STL next to its file", "Export STL",
                0, "ExportStl", "CanExport", 0, itemType));
            for (int i = 0; i < slicers.Count; i++)
            {
                string hint = "Send to " + slicers[i].Name;
                items.Add(grp.AddCommandItem2(slicers[i].Name, -1,
                    "Export the active document and open it in " + slicers[i].Name, hint,
                    i + 1, "SendToSlicer(" + i + ")", "CanExport", i + 1, itemType));
            }

            grp.HasToolbar = true;
            grp.HasMenu = true;
            grp.Activate();

            var ids = new List<int>();
            foreach (int idx in items) ids.Add(grp.get_CommandID(idx));

            foreach (int docType in new[] { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY })
            {
                foreach (string name in new[] { TabName, LegacyTabName })
                {
                    CommandTab old = cmdMgr.GetCommandTab(docType, name);
                    if (old != null) cmdMgr.RemoveCommandTab(old);
                }
                CommandTab tab = cmdMgr.AddCommandTab(docType, TabName);
                const int textBelow = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow;

                // [Export STL] | [slicers...]
                tab.AddCommandTabBox().AddCommands(new[] { ids[0] }, new[] { textBelow });
                if (ids.Count > 1)
                {
                    int[] slicerIds = ids.GetRange(1, ids.Count - 1).ToArray();
                    int[] styles = new int[slicerIds.Length];
                    for (int i = 0; i < styles.Length; i++) styles[i] = textBelow;
                    tab.AddCommandTabBox().AddCommands(slicerIds, styles);
                }
            }
        }

        // One PNG strip per size with an icon per command, as SolidWorks expects.
        // color+letter = slicer icon (colored circle with letter), null letter = STL icon (arrow into tray).
        static string[] BuildIconStrips(List<Color> colors, List<string> letters)
        {
            string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SwToBambu", "icons");
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

        public void ExportStl()
        {
            try
            {
                var doc = swApp.ActiveDoc as ModelDoc2;
                if (doc == null) return;

                string dir = Path.GetDirectoryName(doc.GetPathName());
                string outPath;
                if (string.IsNullOrEmpty(dir))
                {
                    // never saved: ask where to put it
                    using (var dlg = new SaveFileDialog())
                    {
                        dlg.Filter = "STL (*.stl)|*.stl";
                        dlg.FileName = BaseName(doc) + ".stl";
                        if (dlg.ShowDialog() != DialogResult.OK) return;
                        outPath = dlg.FileName;
                    }
                }
                else outPath = Path.Combine(dir, BaseName(doc) + ".stl");

                if (SaveStl(doc, outPath)) Status("Exported " + outPath);
            }
            catch (Exception ex)
            {
                Warn("STL export failed:\n" + ex.Message);
            }
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

                string dir = Path.Combine(Path.GetTempPath(), "SwToBambu");
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
                k.SetValue("Title", "3D Print (Export STL / Send to Slicer)");
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
