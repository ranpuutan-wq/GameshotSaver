#define MyAppName "ゲーム画面キャプチャ"
#define MyAppExe  "ゲーム画面キャプチャ.exe"


; ★ MSBuild/PS から渡す /DMyPubDir="..." を優先。無ければ既定パスにフォールバック
#ifdef MyPubDir
  #define PubDir MyPubDir
#else
  #define PubDir SourcePath + "bin\Release\net8.0-windows\win-x64\publish"
#endif

#define OutDir    SourcePath + "dist"
#define MyAppVer  GetVersionNumbersString(PubDir + "\" + MyAppExe)

  
#ifnexist MyPubDir + "\" + MyAppExe
  #error "Publish フォルダに EXE がありません: " + MyPubDir + "\" + MyAppExe
#endif

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Setup]
AppId={{A2C7C7E1-8D32-41B8-9B6B-FA12-34567890ABCD}} 
AppName={#MyAppName}
AppVersion={#MyAppVer}                       ; バージョン表示は内部に保持

; ★現在ユーザーを基本に。必要ならダイアログで全ユーザーも選べる
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; ★ユーザー用インストール先（昇格なしなら %LOCALAPPDATA%\Programs\）
DefaultDirName={autopf}\{#MyAppName}

; ★前回の Program Files を引き継がない
UsePreviousAppDir=no
UsePreviousPrivileges=no

DefaultGroupName={#MyAppName}
OutputDir={#OutDir}
OutputBaseFilename={#MyAppName}Setup
ArchitecturesInstallIn64BitMode=x64
Compression=lzma
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExe}
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no

[Files]
Source: "{#PubDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Excludes: "*.pdb;*.xml"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "インストール後に起動"; Flags: nowait postinstall skipifsilent
