#define MyAppName "Freight Snap Ingest Service"
#define MyAppPublisher "Systems One"
#define MyAppExeName "FreightSnapIngestService.exe"
#define MyServiceName "FreightSnapIngestService"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{9F1E2C4A-6B3D-4F2E-8A1C-5D7E9B0F3C2A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/Jwagener1/Freight_Snap_Injest_Service
AppSupportURL=https://github.com/Jwagener1/Freight_Snap_Injest_Service/issues
AppUpdatesURL=https://github.com/Jwagener1/Freight_Snap_Injest_Service/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=installer_output
OutputBaseFilename=Freight_Snap_Ingest_Service_Setup
SetupIconFile=Icons\systems_one.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription=Windows Service that ingests freight scan CSVs and barcode images into SQL Server
VersionInfoCopyright=Copyright (C) Systems One
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin

[Files]
; Preserve an existing appsettings.json across upgrades (real deployment config lives there).
Source: "publish\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "publish\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconIndex: 0
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "sc.exe"; Parameters: "stop ""{#MyServiceName}"""; Flags: runhidden skipifdoesntexist waituntilterminated; StatusMsg: "Stopping existing service..."
Filename: "sc.exe"; Parameters: "delete ""{#MyServiceName}"""; Flags: runhidden skipifdoesntexist waituntilterminated; StatusMsg: "Removing existing service..."
Filename: "sc.exe"; Parameters: "create ""{#MyServiceName}"" binPath= ""\""{app}\{#MyAppExeName}\"""" start= auto DisplayName= ""{#MyAppName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Installing service..."
Filename: "sc.exe"; Parameters: "failure ""{#MyServiceName}"" reset= 86400 actions= restart/5000/restart/10000/restart/30000"; Flags: runhidden waituntilterminated; StatusMsg: "Configuring service recovery..."
Filename: "sc.exe"; Parameters: "description ""{#MyServiceName}"" ""Ingests freight scan CSVs and barcode images into SQL Server."""; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "start ""{#MyServiceName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Starting service..."

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop ""{#MyServiceName}"""; Flags: runhidden skipifdoesntexist waituntilterminated
Filename: "sc.exe"; Parameters: "delete ""{#MyServiceName}"""; Flags: runhidden skipifdoesntexist waituntilterminated
