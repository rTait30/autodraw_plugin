# AutoDraw Plugin

A C# AutoCAD plugin for automating drawing tasks using project configuration files.

## Features

- Loads JSON project files
- Automatically draws panels, layouts, and other geometry
- Supports integration with external tools and automated workflows
- Built for future expansion into various drawing tasks

## Requirements

- AutoCAD 202X with .NET plugin support
- The following references must be added manually:
  - `acmgd.dll`
  - `acdbmgd.dll`
  - `accoremgd.dll`

These are typically found in:

C:\Program Files\Autodesk\AutoCAD 202X\


## Setup

1. Open the solution in Visual Studio.
2. Add the required AutoCAD DLLs as references.
3. Build the DLL.
4. Load it into AutoCAD using the `NETLOAD` command.

## Usage

This will be generally used by another program which will use us the autocad COM to send commands to it.

Currently functions include
  - ADLSC (AutoDraw Local Surgical Cover)
	- This will look for a provided project in a provided local folder and draw it
  - ADSSC (AutoDraw Server Surgical Cover)
	- This will fetch a provided project name from a server that has an appropriate endpoint setup
	

## License

MIT / Proprietary / TBD