# build-installer.ps1  (ASCII only, Windows PowerShell 5.1 compatible)
$ErrorActionPreference = "Stop"

# --- Settings ---
$exe = "ゲーム画面キャプチャ.exe"
$rid = "win-x64"
$config = "Release"
$selfContained = $false       # false = runtime-dependent (smaller installer)

# --- Paths ---
$root = $PSScriptRoot
$csproj = Get-ChildItem -Path $root -Filter *.csproj | Select-Object -First 1
if (-not $csproj) { throw "csproj not found: $root" }

# --- Publish (lean) ---
$pubArgs = @(
  "publish", $csproj.FullName,
  "-c", $config, "-r", $rid,
  "-p:SelfContained=$selfContained",
  "-p:PublishSingleFile=false",
  "-p:PublishReadyToRun=false",
  "-p:DebugType=none",
  "-p:SatelliteResourceLanguages=ja"
)
dotnet @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$pubDir  = Join-Path $root "bin\$config\net8.0-windows\$rid\publish"
$exePath = Join-Path $pubDir $exe
if (-not (Test-Path $exePath)) { throw "EXE not found: $exePath" }

# --- Find ISCC.exe (Inno Setup) robust ---
$hint = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
$paths = @(
  $hint,
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and $_.Trim().Length -gt 0 } | Select-Object -Unique

# PATH からの検出も試す
$cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
if ($cmd) {
  foreach ($p in @($cmd.Path, $cmd.Source, $cmd.Definition)) {
    if ($p -and (Test-Path $p)) { $paths += $p }
  }
}
$paths = $paths | Select-Object -Unique

$iscc = $null
foreach ($p in $paths) {
  if (Test-Path $p) { $iscc = $p; break }
}

Write-Host ("ISCC candidate(s):")
$paths | ForEach-Object { Write-Host (" - {0}  [{1}]" -f $_, (Test-Path $_)) }

if (-not $iscc) { throw "ISCC.exe not found. Install Inno Setup 6 or fix the path." }

Remove-Item -Path (Join-Path $root "dist\*.exe") -ErrorAction SilentlyContinue


# --- Inno 実行（& + $LASTEXITCODE で取得 / パスはクォート） ---
$iss    = Join-Path $root "setup.iss"
if (-not (Test-Path $iss)) { throw "setup.iss not found: $iss" }

# 引用符は埋め込まない（PowerShell 7 では内側の " がエスケープされて ISCC に渡り失敗するため）
$define = "/DMyPubDir=$pubDir"

Write-Host ("ISCC : {0}" -f $iscc)
Write-Host ("PubDir: {0}" -f $pubDir)

# 実行（& で直接。パスは必ずダブルクォートで包む）
& "$iscc" $define "$iss"

# --- 実行結果の確認と表示（PS5.1対応） ---
$code = $LASTEXITCODE

$dist = Join-Path $root "dist"
$made = Get-ChildItem -Path $dist -Filter "*.exe" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (($code -ne 0) -and (-not $made)) {
    throw ("ISCC failed with exit code {0}" -f $code)
}

# $made が null なら $dist を表示
$pathToShow = $dist
if ($made -and $made.FullName) { $pathToShow = $made.FullName }

Write-Host ("Installer built: {0}" -f $pathToShow)

