# SwToBambu – 3D Print tab for SolidWorks

A SolidWorks add-in that adds a **3D Print** tab (and menu) to parts and assemblies:

- **Export STL**: saves the active document as an STL next to its file in one click,
  with no dialogs.
- **One button per installed slicer**: exports the model and opens it in that slicer.
  If the slicer is already open, the model is added to the open window instead of
  starting a new one.

Supported slicers (a button only appears if the slicer is installed):

| Slicer | Status |
|---|---|
| Bambu Studio | tested |
| PrusaSlicer | should work |
| OrcaSlicer | should work (same code base as Bambu Studio) |
| UltiMaker Cura | should work |
| Creality Print | should work |

## Requirements

- Windows, 64-bit
- SOLIDWORKS (developed on 2025; any recent version with the .NET API should work)
- At least one of the slicers above if you want the "send to" buttons
- .NET Framework 4.x (preinstalled on Windows 10/11). Visual Studio is **not** required.

## Install

1. Download or clone this repository.
2. Close SolidWorks.
3. Double-click **`install.cmd`** and accept the admin prompt. It compiles the add-in
   against your local SolidWorks API and registers it.
4. Start SolidWorks. The **3D Print** tab appears in parts and assemblies. If it doesn't,
   enable the add-in under **Tools → Add-Ins**.

To remove it, run **`uninstall.cmd`**.

If SolidWorks is installed somewhere unusual and the build can't find it, run
`powershell -ExecutionPolicy Bypass -File build.ps1 -SolidWorksDir "D:\path\to\SOLIDWORKS"`
first, then run `install.cmd`.

Slicers are detected each time SolidWorks starts. After installing a new slicer,
restart SolidWorks and its button appears.

## How it works

- **STL format:** STLs are always binary, in millimetres, and a single file for assemblies.
  Your SolidWorks STL export settings are restored afterwards. If you're not using the
  Default configuration, its name is added to the file name.
- **Export STL** writes `<part folder>\<part name>.stl` and overwrites any existing file.
  Unsaved documents ask for a location.
- **Send to slicer** writes to `%TEMP%\SwToBambu\` and starts the slicer with
  `--single-instance <file>`.

### Slicer not detected?

Slicers are found through the Windows *Installed apps* list and the default
Program Files folders. For portable or unusual installs, set the path manually:

```
reg add HKCU\Software\SwToBambu\Slicers /v "OrcaSlicer" /d "D:\Tools\OrcaSlicer\orca-slicer.exe"
```

The value name must match the button name (`Bambu Studio`, `OrcaSlicer`, `PrusaSlicer`,
`Cura`, `Creality Print`).

### Adding another slicer

Add an entry to `Slicer.All` in [`src/Slicers.cs`](src/Slicers.cs) with its name,
installer display name, exe name, default folder, and icon color and letter.
Then run `install.cmd` again.

## Tips

- **Mesh quality** comes from your SolidWorks STL export settings
  (*File → Save As → STL → Options*). Pick *Fine* or *Custom* if curves look faceted.
- To rebuild after changing the code, close SolidWorks first, because it locks the DLL.
