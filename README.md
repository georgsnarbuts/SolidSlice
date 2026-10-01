# SwToBambu – Send to Bambu Studio from SolidWorks

A SolidWorks add-in that adds a **3D Print** tab (and menu) with one button:
**Send to Bambu Studio**. It exports the active part or assembly to STL and opens it
in Bambu Studio. If Bambu Studio is already running, the model is added to the open
window instead of starting a new one.

## Requirements

- Windows, 64-bit
- SOLIDWORKS (developed on 2025; any recent version with the .NET API should work)
- [Bambu Studio](https://bambulab.com/en/download/studio)
- .NET Framework 4.x (preinstalled on Windows 10/11). Visual Studio is **not** required.

## Install

1. Download or clone this repository.
2. Close SolidWorks.
3. Double-click **`install.cmd`** and accept the admin prompt. It compiles the add-in
   against your local SolidWorks API and registers it.
4. Start SolidWorks. The **3D Print** tab appears in parts and assemblies. If it doesn't,
   enable *Send to Bambu Studio* under **Tools → Add-Ins**.

To remove it, run **`uninstall.cmd`**.

If SolidWorks is installed somewhere unusual and the build can't find it, run
`powershell -ExecutionPolicy Bypass -File build.ps1 -SolidWorksDir "D:\path\to\SOLIDWORKS"`
first, then run `install.cmd`.

## How it works

1. The active document is saved as a copy to `%TEMP%\SwToBambu\<name>.stl`, as a single
   binary file in millimetres. Your SolidWorks STL export settings are restored afterwards.
   If you're not using the Default configuration, its name is added to the file name.
2. Bambu Studio is started with `--single-instance <file>`, which passes the file to an
   already open Bambu Studio window when there is one.

Bambu Studio is located in this order:

1. A path saved earlier (`HKCU\Software\SwToBambu\BambuStudioPath`)
2. The Bambu Studio entry in the installed-programs list
3. `C:\Program Files\Bambu Studio\bambu-studio.exe`
4. Otherwise it asks you to browse for `bambu-studio.exe` once and remembers the answer

## Tips

- **Mesh quality** comes from your SolidWorks STL export settings
  (*File → Save As → STL → Options*). Pick *Fine* or *Custom* if curves look faceted.
- To rebuild after changing the code, close SolidWorks first, because it locks the DLL.
