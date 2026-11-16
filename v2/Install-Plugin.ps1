# Installs the Logi Cool J plugin into the Logi Plugin Service and packs/verifies LogiCoolJ.lplug4.
# Run in PowerShell 7:  pwsh -NoProfile -ExecutionPolicy Bypass -File "C:\Templar Archives\Forge\Logi.Cool.J\v2\Install-Plugin.ps1"
# For the one-class-per-slot fallback:  pwsh -NoProfile -ExecutionPolicy Bypass -File "C:\Templar Archives\Forge\Logi.Cool.J\v2\Install-Plugin.ps1" -SlotForm Actions

param(
    [ValidateSet("Parameters", "Actions")]
    [string]$SlotForm = "Parameters",
    [string]$KeysPath = (Join-Path $PSScriptRoot "keys.yaml")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$V2Root = $PSScriptRoot
$RepoRoot = Split-Path -Parent $V2Root
$KeysFull = [System.IO.Path]::GetFullPath($KeysPath)
if (-not (Test-Path -LiteralPath $KeysFull)) {
    throw "keys.yaml not found at $KeysFull"
}

Push-Location $V2Root
try {
    Write-Host "1. Restoring local LogiPluginTool..."
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed ($LASTEXITCODE)." }

    $Csproj = Join-Path $V2Root "src\LogiCoolJPlugin\LogiCoolJPlugin.csproj"
    Write-Host "2. Building LogiCoolJPlugin (Release, SlotForm=$SlotForm, LinkToPluginService=true)..."
    dotnet build $Csproj -c Release "-p:SlotForm=$SlotForm" "-p:KeysPath=$KeysFull" -p:LinkToPluginService=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)." }

    $ReleaseDir = Join-Path $V2Root "bin\Release\"
    $DistDir = Join-Path $V2Root "dist"
    New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
    $Suffix = if ($SlotForm -eq "Actions") { "-Actions" } else { "" }
    $PackagePath = Join-Path $DistDir "LogiCoolJ$Suffix.lplug4"

    Write-Host "3. Packing $PackagePath..."
    dotnet tool run logiplugintool -- pack $ReleaseDir $PackagePath
    if ($LASTEXITCODE -ne 0) { throw "logiplugintool pack failed ($LASTEXITCODE)." }

    Write-Host "4. Verifying $PackagePath..."
    dotnet tool run logiplugintool -- verify $PackagePath
    if ($LASTEXITCODE -ne 0) { throw "logiplugintool verify failed ($LASTEXITCODE)." }

    $Converter = Join-Path $RepoRoot "lp5-build\yaml2lp5.py"
    if (Test-Path -LiteralPath $Converter) {
        Write-Host "5. Refreshing slot profiles (slots.lp5, slots-actions.lp5, slots-10pages.lp5)..."
        python $Converter --slots $KeysFull (Join-Path $RepoRoot "lp5-build")
        python $Converter --slots $KeysFull $V2Root
    }

    Write-Host "6. Installing $PackagePath with logiplugintool..."
    dotnet tool run logiplugintool -- install $PackagePath
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "logiplugintool install returned $LASTEXITCODE; opening $PackagePath directly (developer .link is also in place)..."
        Start-Process -FilePath $PackagePath
    }

    Write-Host "Done. Import slots.lp5 (or slots-actions.lp5 / slots-10pages.lp5) in Logi Options+."
}
finally {
    Pop-Location
}
