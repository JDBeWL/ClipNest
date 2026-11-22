; 含运行时与不含运行时的安装包使用同一份脚本，差异由发布脚本传入的 /D 定义控制。
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef MyPublishDir
  #define MyPublishDir "..\publish"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\artifacts\installer"
#endif
#ifndef MyOutputBaseFilename
  #define MyOutputBaseFilename "ClipNest-Setup"
#endif
#ifndef MyRequiresDotNet
  #define MyRequiresDotNet "0"
#endif

[Setup]
AppName=ClipNest
AppPublisher=JDBeWL
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\ClipNest
DefaultGroupName=ClipNest
OutputDir={#MyOutputDir}
OutputBaseFilename={#MyOutputBaseFilename}
SetupIconFile=..\src\ClipNest.App\Assets\app.ico
UninstallDisplayIcon={app}\ClipNest.App.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; Inno 默认不会替换版本号相同的文件，同版本重装时主程序会停留在旧内容。
; replacesameversion 在版本相同时比较内容，只有内容不同才覆盖，不会造成降级。
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: replacesameversion recursesubdirs createallsubdirs; Excludes: "README.txt"

[Icons]
Name: "{group}\ClipNest"; Filename: "{app}\ClipNest.App.exe"
Name: "{autodesktop}\ClipNest"; Filename: "{app}\ClipNest.App.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"

[Code]
function HasRuntimeFolder(const RootDir, FrameworkName, VersionPrefix: String): Boolean;
var
  FindRec: TFindRec;
  Root: String;
begin
  Result := False;
  Root := RootDir;
  if (Root <> '') and (Root[Length(Root)] = '\') then
    Root := Copy(Root, 1, Length(Root) - 1);

  if FindFirst(Root + '\shared\' + FrameworkName + '\' + VersionPrefix + '*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function HasDotNetRuntime(const FrameworkName, VersionPrefix: String): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  DotnetRoot: String;
begin
  Result := False;
  if RegGetValueNames(HKLM64,
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\' + FrameworkName,
    Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos(VersionPrefix, Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;

  // SDK 与 Visual Studio 安装的运行时可能不写 sharedfx 注册表项，需要回退到安装目录。
  if HasRuntimeFolder(ExpandConstant('{commonpf64}\dotnet'), FrameworkName, VersionPrefix) then
  begin
    Result := True;
    Exit;
  end;

  if RegQueryStringValue(HKLM64,
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost', 'Path', DotnetRoot) then
    if (DotnetRoot <> '') and HasRuntimeFolder(DotnetRoot, FrameworkName, VersionPrefix) then
    begin
      Result := True;
      Exit;
    end;

  DotnetRoot := GetEnv('DOTNET_ROOT');
  if (DotnetRoot <> '') and HasRuntimeFolder(DotnetRoot, FrameworkName, VersionPrefix) then
    Result := True;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if '{#MyRequiresDotNet}' = '1' then
    if not HasDotNetRuntime('Microsoft.NETCore.App', '10.') then
    begin
      MsgBox('ClipNest 需要 .NET 10 运行时。' + #13#10 +
        '请先安装后再运行安装程序：' + #13#10 +
        'https://dotnet.microsoft.com/download/dotnet/10.0',
        mbError, MB_OK);
      Result := False;
    end;
end;
