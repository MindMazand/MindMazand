; ===================================================================
;  Mazandasht - Inno Setup 6 installer script (G12)
;
;  Build on Windows:
;      "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" Mazandasht_Setup.iss
;  or just run build-windows.bat, which calls this file for you.
;
;  The application is a per-user desktop tool: its database lives in
;  %LOCALAPPDATA%\Mazandasht\Data, so the program itself is installed under
;  %LOCALAPPDATA% and no administrator rights are required.
; ===================================================================

#define MyAppName "Mazandasht"
#define MyAppNameFa "مازندشت"
#define MyAppVersion "0.5.2-alpha3"
#define MyAppPublisher "Mazandasht"
#define MyAppExeName "Mazandasht.Desktop.exe"
#define SourceRoot "..\app\Mazandasht.Desktop\bin\x64\Release"

[Setup]
AppId={{7D3F2A1C-9B46-4E5A-8C21-5A6B7C8D9E01}
AppName={#MyAppNameFa} ({#MyAppName})
AppVersion={#MyAppVersion}
AppVerName={#MyAppNameFa} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Mazandasht
DefaultGroupName={#MyAppNameFa}
DisableProgramPage=yes
DisableWelcomePage=no
OutputDir=Output
OutputBaseFilename=Mazandasht_Setup
SetupIconFile={#SourceRoot}\Assets\Icons\app.ico
Compression=lzma2/max
SolidCompression=yes
; Per-user install: no UAC prompt, matches where the database is stored.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=6.3.9600   ; Windows 8.1
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppNameFa} {#MyAppVersion}
WizardStyle=modern

[Messages]
; Persian labels for the wizard (Inno falls back to English for the rest).
WelcomeLabel1=به نصب‌کنندهٔ {#MyAppNameFa} خوش آمدید
WelcomeLabel2=این برنامه، {#MyAppNameFa} را روی رایانهٔ شما نصب می‌کند.%n%nپیش از ادامه، همهٔ برنامه‌های دیگر را ببندید.
ClickReady=برای شروع نصب، «نصب» را بزنید.

[Tasks]
Name: "desktopicon"; Description: "ساخت میان‌بر روی میزکار"; GroupDescription: "میان‌برها:"; Checkable
Name: "startmenuicon"; Description: "ساخت میان‌بر در منوی شروع"; GroupDescription: "میان‌برها:"

[Dirs]
Name: "{localappdata}\Mazandasht\Data"; Permissions: users-modify
Name: "{localappdata}\Mazandasht\Backups"; Permissions: users-modify
Name: "{localappdata}\Mazandasht\Exports"; Permissions: users-modify

[Files]
Source: "{#SourceRoot}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\{#MyAppExeName}.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\mazandasht-logo.png"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\x64\SQLite.Interop.dll"; DestDir: "{app}\x64"; Flags: ignoreversion
Source: "{#SourceRoot}\x86\SQLite.Interop.dll"; DestDir: "{app}\x86"; Flags: ignoreversion
Source: "{#SourceRoot}\Database\*.sql"; DestDir: "{app}\Database"; Flags: ignoreversion
Source: "{#SourceRoot}\Templates\*.xlsx"; DestDir: "{app}\Templates"; Flags: ignoreversion
Source: "{#SourceRoot}\Templates\Themes\*.xlsx"; DestDir: "{app}\Templates\Themes"; Flags: ignoreversion recursesubdirs
Source: "{#SourceRoot}\Assets\Fonts\*"; DestDir: "{app}\Assets\Fonts"; Flags: ignoreversion
Source: "{#SourceRoot}\Assets\Icons\*"; DestDir: "{app}\Assets\Icons"; Flags: ignoreversion
Source: "{#SourceRoot}\Assets\Themes\*"; DestDir: "{app}\Assets\Themes"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppNameFa}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{group}\حذف نصب {#MyAppNameFa}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppNameFa}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "اجرای {#MyAppNameFa}"; Flags: nowait postinstall skipifsilent

; The database lives in %LOCALAPPDATA%\Mazandasht\Data, outside the install
; folder, so uninstalling never touches the owner's data.
