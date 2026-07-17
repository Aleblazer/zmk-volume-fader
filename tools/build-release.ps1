[CmdletBinding()]
param(
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$appProject = Join-Path $root 'ZmkVolumeFader\ZmkVolumeFader.csproj'
$testProject = Join-Path $root 'ZmkVolumeFader.Tests\ZmkVolumeFader.Tests.csproj'
$installerProject = Join-Path $root 'Installer\ZmkVolumeFader.Installer.wixproj'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml] $projectXml = Get-Content -Raw -LiteralPath $appProject
    $Version = [string] $projectXml.Project.PropertyGroup.Version
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use major.minor.patch format; received '$Version'."
}

$publishDir = Join-Path $root 'artifacts\publish'
$releaseDir = Join-Path $root 'artifacts\release'
New-Item -ItemType Directory -Force -Path $publishDir, $releaseDir | Out-Null

$portableName = "ZmkVolumeFader-$Version-win-x64.exe"
$installerName = "ZmkVolumeFader-$Version-win-x64.msi"
$portablePath = Join-Path $releaseDir $portableName
$installerPath = Join-Path $releaseDir $installerName
$checksumPath = Join-Path $releaseDir 'SHA256SUMS.txt'
Remove-Item -LiteralPath $portablePath, $installerPath, $checksumPath -Force -ErrorAction SilentlyContinue

Push-Location $root
try {
    dotnet restore $appProject -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Application restore failed.' }
    dotnet restore $testProject
    if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }
    dotnet restore $installerProject
    if ($LASTEXITCODE -ne 0) { throw 'Installer restore failed.' }

    dotnet build $appProject -c Release --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    dotnet run --project $testProject -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Logic tests failed.' }

    dotnet publish $appProject -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'Portable application publish failed.' }

    dotnet build $installerProject -c Release --no-restore `
        -p:ProductVersion=$Version `
        -p:PublishDir=$publishDir
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

    Copy-Item -LiteralPath (Join-Path $publishDir 'ZmkVolumeFader.exe') -Destination $portablePath
    $builtInstaller = Get-ChildItem -LiteralPath (Join-Path $root 'Installer\bin') `
        -Filter "ZmkVolumeFader-$Version-win-x64.msi" -File -Recurse |
        Select-Object -First 1
    if ($null -eq $builtInstaller) {
        throw 'Installer build completed but the MSI could not be found.'
    }
    Copy-Item -LiteralPath $builtInstaller.FullName -Destination $installerPath

    $checksumLines = @($installerPath, $portablePath) | ForEach-Object {
        $file = Get-Item -LiteralPath $_
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($file.Name)"
    }
    Set-Content -LiteralPath $checksumPath -Value $checksumLines -Encoding ascii
}
finally {
    Pop-Location
}

Write-Host "Release assets created in $releaseDir"
Get-Item -LiteralPath $installerPath, $portablePath, $checksumPath |
    Select-Object Name, Length, LastWriteTime
