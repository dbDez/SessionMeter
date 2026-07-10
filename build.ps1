# build.ps1 — SessionMeter (Session.exe) — self-contained single-file + Inno installer that adds it to PATH.
#
# SessionMeter is a keyless, portable consumer CLI (not an internal desktop app), so it ships
# self-contained single-file: no .NET runtime dependency, works on any Win-x64 box, and the hooks
# that call `session` frequently never hit a runtime-resolution edge case. (Deviates from the
# dotnet-installer LW default deliberately — the "avoid N runtime copies" rationale for LW does not
# apply to a single portable tool.)
#
# Usage:  pwsh -NoProfile -File .\build.ps1
# Output: dist\installer\SessionMeter-Setup-<ver>.exe (in the main checkout, even from a worktree).

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectRoot

# --- ISCC discovery ---------------------------------------------------------
$candidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe not found (Inno Setup 6). Install it, then re-run." }

# --- Resolve canonical dist\installer (main checkout, not a worktree) --------
$canonicalRoot = $ProjectRoot
try {
    $commonGitDir = (& git -C $ProjectRoot rev-parse --path-format=absolute --git-common-dir 2>$null).Trim()
    if ($LASTEXITCODE -eq 0 -and $commonGitDir -and (Test-Path $commonGitDir)) {
        $candidate = Split-Path -Parent $commonGitDir
        if ($candidate -and (Test-Path $candidate)) { $canonicalRoot = $candidate }
    }
} catch { }
$LASTEXITCODE = 0
$DistDir = Join-Path $canonicalRoot "dist\installer"
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
Write-Host "==> Installer output: $DistDir" -ForegroundColor Cyan

# --- Version sync: csproj is the single source of truth ---------------------
[xml]$csproj = Get-Content "$ProjectRoot\src\Session\Session.csproj" -Raw
$projVersion = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).ToString().Trim()
if (-not $projVersion -or $projVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Could not read a valid <Version> from src\Session\Session.csproj (got: '$projVersion')"
}
Write-Host "==> Project version: $projVersion (synced into SessionMeter.iss)" -ForegroundColor Cyan
$issPath = Join-Path $ProjectRoot "SessionMeter.iss"
$text = Get-Content $issPath -Raw
$newText = [regex]::Replace($text, '(?m)^(#define\s+MyAppVersion\s+")[^"]*(")', "`${1}$projVersion`${2}")
if ($newText -ne $text) { Set-Content -Path $issPath -Value $newText -NoNewline -Encoding UTF8; Write-Host "    Updated SessionMeter.iss" -ForegroundColor DarkGray }

# --- Publish self-contained single-file -------------------------------------
Write-Host "==> Publishing self-contained single-file (win-x64)..." -ForegroundColor Cyan
dotnet publish "src\Session\Session.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "bin\Release\publish-sc"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# --- Compile the installer --------------------------------------------------
& $iscc "/O$DistDir" "SessionMeter.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$out = Join-Path $DistDir "SessionMeter-Setup-$projVersion.exe"
if (Test-Path $out) {
    $mb = [math]::Round((Get-Item $out).Length / 1MB, 1)
    Write-Host "`n==> Built: $out ($mb MB)" -ForegroundColor Green
} else {
    throw "Installer not found at $out after build"
}
