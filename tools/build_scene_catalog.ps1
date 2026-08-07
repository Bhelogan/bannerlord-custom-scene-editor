<#
.SYNOPSIS
    Builds the scene catalog for the Custom Bannerlord Scene Creator.

.DESCRIPTION
    Enumerates every SceneObj folder across the installed game modules and records what the editor
    needs in order to open each scene: its owning module, its category, and - critically - its
    upgrade level names.

    Level names are read straight out of each scene's own scene.xscene, which is plain XML and
    declares them explicitly:

        <levels>
          <level name="base"    mask="1"/>
          <level name="level_1" mask="2"/>
          ...
        </levels>

    That matters because town, castle and village scenes are multi-level: opening one with an empty
    SceneLevels string renders it wrong or blank. Harvesting from the scene itself means no guessing
    and no cross-referencing settlements.xml.

    Also records which support files each scene has. A scene missing navmesh.bin will load but no AI
    can path in it, which is worth knowing before a user reports it as a bug.

.PARAMETER GameDir
    Bannerlord install root. Defaults to the known local install.

.PARAMETER OutFile
    Where to write the catalog. Defaults to the module's ModuleData folder.

.EXAMPLE
    pwsh -File tools/build_scene_catalog.ps1
#>
[CmdletBinding()]
param(
    [string] $GameDir = 'F:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [string] $OutFile = ''
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not reliably populated inside a param() default under Windows PowerShell 5.1,
# so resolve the script directory here instead.
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptDir)) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrEmpty($OutFile)) {
    $OutFile = Join-Path $scriptDir '..\CustomSceneCreator\_Module\ModuleData\scene_catalog.xml'
}

$modulesRoot = Join-Path $GameDir 'Modules'
if (-not (Test-Path $modulesRoot)) {
    throw "Modules folder not found at '$modulesRoot'. Pass -GameDir with your install path."
}

# Categories are matched in order, first hit wins. Order matters: 'battle_terrain_coastal_*' must be
# claimed by Battle Terrain before anything tries to read 'coastal' as naval, and the mp_ prefix must
# win over any settlement word appearing later in a multiplayer map name.
$categoryRules = @(
    @{ Name = 'Multiplayer';    Pattern = '^mp_' }
    @{ Name = 'Battle Terrain'; Pattern = '^battle_terrain|^coastal_terrain' }
    @{ Name = 'Arena';          Pattern = 'arena' }
    @{ Name = 'Hideout';        Pattern = 'hideout' }
    @{ Name = 'Town';           Pattern = 'town' }
    @{ Name = 'Castle';         Pattern = 'castle' }
    @{ Name = 'Village';        Pattern = 'village' }
    @{ Name = 'Interior';       Pattern = 'interior|tavern|dungeon|keep|house|shop|prison' }
    @{ Name = 'Naval';          Pattern = 'ship|shipyard|opensea|naval|port' }
    @{ Name = 'Menu & Cutscene';Pattern = '^main_menu|^character_|^inventory_|cutscene|^ibl_|popup|benchmark' }
    @{ Name = 'World Map';      Pattern = '^Main_map$' }
)

function Get-SceneCategory([string] $sceneName) {
    foreach ($rule in $categoryRules) {
        if ($sceneName -match $rule.Pattern) { return $rule.Name }
    }
    return 'Other'
}

$scenes = [System.Collections.Generic.List[object]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

foreach ($moduleDir in Get-ChildItem -Path $modulesRoot -Directory) {
    $sceneObj = Join-Path $moduleDir.FullName 'SceneObj'
    if (-not (Test-Path $sceneObj)) { continue }

    foreach ($sceneDir in Get-ChildItem -Path $sceneObj -Directory) {
        $name = $sceneDir.Name

        # A scene name can legitimately appear in more than one module (NavalDLC re-ships Main_map,
        # for instance). The engine resolves by load order; we keep the first and record the clash
        # rather than emitting a duplicate the UI would show twice.
        if (-not $seen.Add($name)) {
            Write-Verbose "Duplicate scene '$name' in $($moduleDir.Name) - keeping first occurrence."
            continue
        }

        $xscene   = Join-Path $sceneDir.FullName 'scene.xscene'
        $levels   = @()
        $version  = ''
        $hasScene = Test-Path $xscene

        if ($hasScene) {
            try {
                [xml] $doc = Get-Content -LiteralPath $xscene -Raw
                $version = [string] $doc.scene.version
                if ($doc.scene.levels -and $doc.scene.levels.level) {
                    $levels = @($doc.scene.levels.level | ForEach-Object { [string] $_.name })
                }
            } catch {
                # Some scenes ship malformed or truncated XML. Record them rather than aborting the
                # whole catalog over one bad file.
                Write-Warning "Could not parse '$xscene': $($_.Exception.Message)"
            }
        }

        $scenes.Add([pscustomobject] @{
            Name        = $name
            Module      = $moduleDir.Name
            Category    = Get-SceneCategory $name
            Version     = $version
            Levels      = $levels
            HasTerrain  = Test-Path (Join-Path $sceneDir.FullName 'terrain.bin')
            HasNavMesh  = Test-Path (Join-Path $sceneDir.FullName 'navmesh.bin')
            HasFlora    = Test-Path (Join-Path $sceneDir.FullName 'flora.bin')
            HasAtmos    = Test-Path (Join-Path $sceneDir.FullName 'atmosphere.xml')
            HasScene    = $hasScene
        })
    }
}

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)

$writer = [System.Xml.XmlWriter]::Create((Resolve-Path -LiteralPath $outDir).Path + '\' + (Split-Path -Leaf $OutFile), $settings)
try {
    $writer.WriteStartDocument()
    $writer.WriteComment(" Generated by tools/build_scene_catalog.ps1 on $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ")
    $writer.WriteComment(" Source install: $GameDir ")
    $writer.WriteComment(" Do not hand-edit: regenerate instead. Curated overrides belong in scene_overrides.xml. ")
    $writer.WriteStartElement('SceneCatalog')

    foreach ($s in ($scenes | Sort-Object Category, Name)) {
        $writer.WriteStartElement('Scene')
        $writer.WriteAttributeString('name',     $s.Name)
        $writer.WriteAttributeString('module',   $s.Module)
        $writer.WriteAttributeString('category', $s.Category)
        if ($s.Version)      { $writer.WriteAttributeString('version', $s.Version) }
        # Space-separated: this is the exact form MissionInitializerRecord.SceneLevels expects.
        if ($s.Levels.Count) { $writer.WriteAttributeString('levels', ($s.Levels -join ' ')) }
        if (-not $s.HasNavMesh) { $writer.WriteAttributeString('noNavMesh', 'true') }
        if (-not $s.HasTerrain) { $writer.WriteAttributeString('noTerrain', 'true') }
        if (-not $s.HasAtmos)   { $writer.WriteAttributeString('noAtmosphere', 'true') }
        if (-not $s.HasScene)   { $writer.WriteAttributeString('noSceneXml', 'true') }
        $writer.WriteEndElement()
    }

    $writer.WriteEndElement()
    $writer.WriteEndDocument()
} finally {
    $writer.Flush(); $writer.Close()
}

Write-Host "Wrote $($scenes.Count) scenes to $OutFile"
Write-Host ''
Write-Host 'By category:'
$scenes | Group-Object Category | Sort-Object Count -Descending |
    ForEach-Object { '{0,-18} {1,4}' -f $_.Name, $_.Count }
Write-Host ''
Write-Host ('Multi-level scenes: {0}' -f (@($scenes | Where-Object { $_.Levels.Count -gt 1 }).Count))
Write-Host ('Missing navmesh:    {0}' -f (@($scenes | Where-Object { -not $_.HasNavMesh }).Count))
Write-Host ('Missing atmosphere: {0}' -f (@($scenes | Where-Object { -not $_.HasAtmos }).Count))
