<#
.SYNOPSIS
    Packages a release: one zip with everything a user needs.

.DESCRIPTION
    Three things used to be shared separately - the module folder, the user manual, and the tools.
    Anyone who got two of the three had a broken install or no documentation, and there was nothing
    in the zip to say which version it was. This produces one file:

        CustomSceneCreator-v1.0.4.zip
          CustomSceneCreator/          <- drop this straight into Modules
          USER_MANUAL.htm
          MOD_INTEGRATION.md            <- using exports in another mod
          README.txt                   <- install instructions, generated
          LICENSE
          tools/                       <- bake script and the catalog generators

    The module folder sits at the top level of the zip on purpose: "extract this into your Modules
    folder" is one instruction, and it is the one people get right.

.PARAMETER SkipBuild
    Package whatever is already in Dist rather than rebuilding first.

.PARAMETER OutputDir
    Where to write the zip. Defaults to a Release folder in the repo root.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/package_release.ps1
    powershell -ExecutionPolicy Bypass -File tools/package_release.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [switch] $SkipBuild,
    [string] $OutputDir = ''
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptDir)) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = Resolve-Path (Join-Path $scriptDir '..')

if ([string]::IsNullOrEmpty($OutputDir)) { $OutputDir = Join-Path $repoRoot 'Release' }

$project  = Join-Path $repoRoot 'CustomSceneCreator\CustomSceneCreator.csproj'
$distDir  = Join-Path $repoRoot 'Dist\CustomSceneCreator'

# ---------------------------------------------------------------------------------------------
# Version, read from the project file so the zip name cannot drift from what was built
# ---------------------------------------------------------------------------------------------

[xml] $projectXml = Get-Content $project
$version = ($projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ([string]::IsNullOrWhiteSpace($version)) { $version = '0.0.0' }

Write-Host "Packaging Custom Scene Creator v$version"

# ---------------------------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------------------------

if (-not $SkipBuild) {
    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

    Write-Host 'Building (Release, x64)...'
    & $dotnet build $project -c Release -p:Platform=x64 -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path $distDir)) {
    throw "No module folder at '$distDir'. Build first, or drop -SkipBuild."
}

# A module with no DLL installs cleanly and then does nothing at all, which is a miserable thing to
# hand someone. Fail here instead.
$dll = Join-Path $distDir 'bin\Win64_Shipping_Client\CustomSceneCreator.dll'
if (-not (Test-Path $dll)) { throw "No CustomSceneCreator.dll in '$distDir'. The build did not produce one." }

foreach ($required in @('ModuleData\bannerlord_assets_v1.4.7.txt',
                        'ModuleData\scene_catalog.xml',
                        'ModuleData\script_catalog.xml',
                        'ModuleData\packs\csc_core.xml',
                        'SubModule.xml')) {
    if (-not (Test-Path (Join-Path $distDir $required))) {
        throw "Missing '$required' in the module folder. Regenerate it with the scripts in tools/."
    }
}

# SubModule.xml is the one file whose failure is total: the launcher refuses the whole module and
# says only "can't be loaded, there are some errors" with a line number. A single unterminated
# attribute quote is enough, and nothing else in the build notices.
try { [xml] (Get-Content (Join-Path $distDir 'SubModule.xml') -Raw) | Out-Null }
catch { throw "SubModule.xml is not valid XML: $($_.Exception.Message)" }

[xml] $moduleXml = Get-Content (Join-Path $distDir 'SubModule.xml') -Raw
$moduleVersion = [string] $moduleXml.Module.Version.value
if ($moduleVersion.TrimStart('v') -ne $version) {
    throw "Version mismatch: project is $version but packaged SubModule.xml is $moduleVersion."
}

# A pack that will not parse costs every marker in it, and the failure is invisible until someone
# opens the editor and finds the category missing. Cheap to check here.
Get-ChildItem (Join-Path $distDir 'ModuleData\packs') -Filter *.xml | ForEach-Object {
    try { [xml] (Get-Content $_.FullName -Raw) | Out-Null }
    catch { throw "Marker pack '$($_.Name)' is not valid XML: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------------------------------------
# Staging
# ---------------------------------------------------------------------------------------------

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "csc_release_$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $staging | Out-Null

try {
    Copy-Item $distDir (Join-Path $staging 'CustomSceneCreator') -Recurse

    foreach ($file in @('USER_MANUAL.htm', 'MOD_INTEGRATION.md', 'LICENSE')) {
        $source = Join-Path $repoRoot $file
        if (-not (Test-Path $source)) { throw "Missing release document '$file'." }
        Copy-Item $source $staging
    }

    # Keep the copies inside the installed module identical to the top-level release documents even
    # when -SkipBuild packages an older Dist staging folder.
    Copy-Item (Join-Path $repoRoot 'USER_MANUAL.htm') (Join-Path $staging 'CustomSceneCreator\USER_MANUAL.htm') -Force
    Copy-Item (Join-Path $repoRoot 'MOD_INTEGRATION.md') (Join-Path $staging 'CustomSceneCreator\MOD_INTEGRATION.md') -Force

    # Two kinds of tool, kept apart. The bake script is something a user reaches for as soon as they
    # have exported something; the catalog generators are for two narrow cases (a game update, or
    # pulling your own mod's prefabs into the picker). Six scripts in one flat folder made the common
    # one look like an option among equals.
    $toolsOut = Join-Path $staging 'tools'
    New-Item -ItemType Directory -Force -Path $toolsOut | Out-Null
    foreach ($file in @('bake_scene.py', 'bake_config.json', 'README_BAKE.md',
                        'navmesh_inspect.py', 'navmesh_cutout.py', 'navmesh_addition_plan.py',
                        'navmesh_apply_cutout.py', 'navmesh_apply_addition.py',
                        'navmesh_workflow.py', 'validate_navmeshes.py',
                        'check_scene_folders.py', 'README_NAVMESH.md')) {
        $source = Join-Path $scriptDir $file
        if (Test-Path $source) { Copy-Item $source $toolsOut }
    }

    $regenOut = Join-Path $toolsOut 'regenerate'
    New-Item -ItemType Directory -Force -Path $regenOut | Out-Null
    foreach ($file in @('README_REGENERATE.md',
                        'build_asset_dump.ps1', 'build_scene_catalog.ps1', 'build_script_catalog.ps1')) {
        $source = Join-Path $scriptDir $file
        if (Test-Path $source) { Copy-Item $source $regenOut }
    }

    # Install instructions in the zip itself. The manual covers everything, but someone who has just
    # downloaded a zip wants four lines, not a document.
    $readme = @"
Custom Scene Creator v$version
For Mount & Blade II: Bannerlord v1.4.7

INSTALLING
  1. Copy the CustomSceneCreator folder into:
       ...\Mount & Blade II Bannerlord\Modules\
  2. Enable "Custom Bannerlord Scene Creator" in the launcher's Singleplayer tab.
  3. Load a campaign, enter any town, village or castle, and choose
     "Open Scene Creator" on the settlement menu.

  Optional: install Mod Configuration Menu (MCM) if you want to rebind keys.
  Everything works without it.

FIRST STEPS
  Press \ to start building, ` to choose what to build, and left-click to place.
  Open USER_MANUAL.htm in a browser for building, navmesh authoring, baking,
  Walk Around following tests, Raid-Scale Battle tests, and troubleshooting.

TESTING SAVED PROJECTS
  Select a saved project before choosing a test mode.
  Walk Around restores the project and spawns all healthy companion heroes plus
  five additional Empire peasants, all on foot and following you.
  Raid-Scale Battle restores the same project and baked navmesh, then copies your
  healthy party against a matching on-foot bandit/looter force (100 per side max).
  With cutouts, the sides start across the obstacle cluster at least 200 m apart.
  Neither mode changes the campaign roster or casualties. Test-slot scene folders
  are disposable caches and must not be shipped. Read MOD_INTEGRATION.md for the
  exact traversal, combat-routing, and release checks.

USING OUTPUT IN YOUR OWN MOD
  Read MOD_INTEGRATION.md before shipping anything. It explains the difference
  between prefabs, templates, scene fragments, complete scenes, readable
  .navcut.json plans, and runtime navmesh.bin files.

WHAT YOU MAKE
  Your work is saved under
    Documents\Mount and Blade II Bannerlord\CustomSceneCreator\
  Nothing is written into your campaign save, and uninstalling the mod does not
  touch your scenes.

TOOLS
  tools\bake_scene.py post-processes an export. You do not need it to start -
  see tools\README_BAKE.md.

  Navmesh authoring and release baking are built into CSC. Use Cutout, Add Area,
  and Elevated Navmesh, then Save or Rebake Navmesh. The tools\navmesh_*.py
  scripts are advanced inspection/regression utilities; they do not apply
  elevated routes. Read tools\README_NAVMESH.md before using them.

  PowerShell scripts here must be run as:
      powershell -ExecutionPolicy Bypass -File <script>.ps1
  Windows refuses unsigned scripts otherwise. That applies to the one run
  only and changes nothing on your system.

  tools\regenerate\ rebuilds the asset and scene catalogs. Two reasons to:
  the game updated, or you want YOUR OWN mod's prefabs to show up in the
  editor's asset picker. See tools\regenerate\README_REGENERATE.md.

LICENSE
  MIT. See LICENSE.
"@
    Set-Content -Path (Join-Path $staging 'README.txt') -Value $readme -Encoding utf8

    # -----------------------------------------------------------------------------------------
    # Zip
    # -----------------------------------------------------------------------------------------

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $zipPath = Join-Path $OutputDir "CustomSceneCreator-v$version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal

    $sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host ''
    Write-Host "Wrote $zipPath ($sizeMb MB)"
    Write-Host ''
    Write-Host 'Contents:'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $archive.Entries |
            Group-Object { ($_.FullName -split '[\\/]')[0] } |
            Sort-Object Name |
            ForEach-Object { '  {0,-28} {1} file(s)' -f $_.Name, $_.Count }
    } finally { $archive.Dispose() }
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
