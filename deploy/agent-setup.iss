; LeagueTracker Agent installer (Inno Setup 6). Built by deploy/publish-agent.ps1
; when ISCC is available (always in CI): a per-user install into
; %LocalAppData%\Programs\LeagueTracker Agent, no admin, Start Menu entry,
; Settings > Apps entry, and the agent's own setup window at the end.
; The zip stays the update channel: the agent replaces its own files in
; place, which a per-user folder allows.
;
; Defines expected: AppVersion (yyyy.Mdd.HHmm.ss), SourceDir (published files), OutDir.

#ifndef AppVersion
  #define AppVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir must be defined
#endif
#ifndef OutDir
  #define OutDir "."
#endif

[Setup]
AppId={{7E1D3C5A-8B4F-4B7C-9C1E-2A6F5D8B9C01}
AppName=LeagueTracker Agent
AppVersion={#AppVersion}
AppVerName=LeagueTracker Agent {#AppVersion}
AppPublisher=LeagueTracker
AppPublisherURL=https://github.com/r-alvarez/LeagueTracker
DefaultDirName={localappdata}\Programs\LeagueTracker Agent
DefaultGroupName=LeagueTracker
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutDir}
OutputBaseFilename=LeagueTracker.Agent-Setup-{#AppVersion}
SetupIconFile={#SourceDir}\..\..\Assets\app.ico
UninstallDisplayIcon={app}\LeagueTracker.RenderAgent.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#SourceDir}\LeagueTracker.RenderAgent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LeagueTracker.ReplayLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\ScreenRecorderLib.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\appsettings.template.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
; Marks "installed by Setup": the agent's own --install then leaves the
; Start Menu / Apps entries to Inno instead of making a second pair.
Source: "{#SourceDir}\appsettings.template.json"; DestDir: "{app}"; DestName: "setup.installed"; Flags: ignoreversion

[Icons]
Name: "{group}\LeagueTracker Agent"; Filename: "{app}\LeagueTracker.RenderAgent.exe"; Parameters: "--setup"; Comment: "LeagueTracker agent settings"
Name: "{group}\Uninstall LeagueTracker Agent"; Filename: "{uninstallexe}"

[Run]
; The agent's own setup window: tracker URL, role, folder - then it registers
; run-at-logon and starts. Shown as the last wizard step.
Filename: "{app}\LeagueTracker.RenderAgent.exe"; Parameters: "--install"; Description: "Set up the agent now"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Stops a running agent politely and removes the run-at-logon entry before
; the files go.
Filename: "{app}\LeagueTracker.RenderAgent.exe"; Parameters: "--uninstall --quiet"; RunOnceId: "AgentUninstall"; Flags: waituntilterminated

[UninstallDelete]
Type: files; Name: "{app}\agent.log"
Type: files; Name: "{app}\stop.requested"
Type: files; Name: "{app}\paused"
Type: filesandordirs; Name: "{app}\update"
; agent.key, appsettings.json and youtube-token.json are deliberately kept:
; a reinstall picks the machine's identity and settings back up.
