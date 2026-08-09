<#
.SYNOPSIS
    Regenerates the Bannerlord prefab asset dump for the installed game version.

.DESCRIPTION
    Walks every Modules/*/Prefabs/*.xml and emits one row per top-level <game_entity>, which is what
    GameEntity.Instantiate / GameEntity.PrefabExists take by name.

    The column layout is deliberately identical to the old bannerlord_assets_v1.3.15.txt, because
    existing tooling indexes it positionally - the bake scripts read PhysicsShapes at index 7 and
    Meshes at index 11. Changing the layout would silently break them.

    Meshes matter beyond display: a prefab with no mesh is a marker or a logic node rather than
    something you can see. Those are NOT dropped here - they are the spawn points, patrol nodes and
    animation points the editor wants to place, and there are hundreds of them. Filtering is the
    consumer's job.

.PARAMETER GameDir
    Bannerlord install root.

.PARAMETER OutFile
    Where to write the dump. Defaults to the module's ModuleData folder, version-stamped.

.EXAMPLE
    powershell -File tools/build_asset_dump.ps1
#>
[CmdletBinding()]
param(
    [string] $GameDir = 'F:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [string] $OutFile = '',
    [switch] $IncludeAllModules
)

$ErrorActionPreference = 'Stop'

# Where the generated file belongs.
#
# Two layouts have to work: the source repo, where it goes into the module's staged ModuleData, and
# an installed game, where it goes into the module the game actually loads. Writing the repo path
# blindly would create a stray folder next to the script and leave the game still using the shipped
# catalog, with nothing to show anything had gone wrong.
function Resolve-ModuleDataDir($scriptDir, $gameDir) {
    $repo = Join-Path $scriptDir '..\CustomSceneCreator\_Module\ModuleData'
    if (Test-Path (Split-Path -Parent $repo)) { return $repo }

    $installed = Join-Path $gameDir 'Modules\CustomSceneCreator\ModuleData'
    if (Test-Path (Split-Path -Parent $installed)) { return $installed }

    throw "Could not find the Custom Scene Creator module. Pass -OutFile with where to write, or -GameDir with your install path."
}

$scriptDir = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptDir)) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }

$modulesRoot = Join-Path $GameDir 'Modules'
if (-not (Test-Path $modulesRoot)) {
    throw "Modules folder not found at '$modulesRoot'. Pass -GameDir with your install path."
}

# ONLY official modules. The dump ships with the mod, so anything scanned here becomes a placeable
# that every user is offered - and a prefab from a third-party mod they do not have is a dead entry
# that fails to instantiate. Scanning whatever happened to be installed on the machine that built
# the dump is not a reasonable thing to redistribute.
#
# Users can still get their own mods' prefabs: pass -IncludeAllModules to build a local dump.
$officialModules = @(
    'Native'
    'SandBox'
    'SandBoxCore'
    'StoryMode'
    'Multiplayer'
    'CustomBattle'
    'BirthAndDeath'
    'NavalDLC'
)

# Version comes from Native's SubModule.xml so the filename always matches what it was built from.
$gameVersion = 'unknown'
try {
    [xml] $nativeSub = Get-Content -LiteralPath (Join-Path $modulesRoot 'Native\SubModule.xml') -Raw
    $gameVersion = ([string] $nativeSub.Module.Version.value).TrimStart('v')
} catch {
    Write-Warning "Could not read Native/SubModule.xml version: $($_.Exception.Message)"
}

if ([string]::IsNullOrEmpty($OutFile)) {
    $OutFile = Join-Path (Resolve-ModuleDataDir $scriptDir $GameDir) "bannerlord_assets_v$gameVersion.txt"
}

# Category is inferred from the prefab FILE name, which is how the game groups its own assets -
# archhitecture_aserai.xml is all architecture, nature_*.xml is all vegetation, and so on. Matched in
# order, first hit wins.
$categoryRules = @(
    @{ Name = 'architecture'; Pattern = 'archhitecture|architecture|building|house|castle|town|village|wall|interior' }
    @{ Name = 'vegetation';   Pattern = 'nature|tree|plant|flora|bush|grass' }
    @{ Name = 'terrain';      Pattern = 'terrain|rock|cliff|ground|stone' }
    @{ Name = 'siege';        Pattern = 'siege|ram|ladder|mangonel|ballista|trebuchet|catapult' }
    @{ Name = 'naval';        Pattern = 'ship|boat|naval|sail|harbor|harbour|dock' }
    @{ Name = 'furniture';    Pattern = 'furniture|table|chair|bed|bench|shelf|interior_prop' }
    @{ Name = 'lighting';     Pattern = 'light|lamp|torch|candle|fire|brazier' }
    @{ Name = 'banner';       Pattern = 'banner|flag|heraldr' }
    @{ Name = 'marker';       Pattern = 'spawn|editor|marker|navigation|patrol' }
    @{ Name = 'animal';       Pattern = 'animal|horse|cow|sheep|goat|chicken|dog|pig' }
    @{ Name = 'prop';         Pattern = 'prop|item|misc|clutter|barrel|crate|basket|pottery|tool' }
)

function Get-AssetCategory([string] $prefabFileName) {
    foreach ($rule in $categoryRules) {
        if ($prefabFileName -match $rule.Pattern) { return $rule.Name }
    }
    return 'misc'
}

# Collapses a set of values into the comma-joined form the dump uses, de-duplicated and capped so a
# pathological prefab cannot produce a megabyte-wide row.
function Join-Values($values, [int] $max = 12) {
    $distinct = @($values | Where-Object { $_ } | Select-Object -Unique)
    if ($distinct.Count -eq 0) { return '' }
    if ($distinct.Count -gt $max) {
        return (($distinct | Select-Object -First $max) -join ',') + ",+$($distinct.Count - $max) more"
    }
    return $distinct -join ','
}

function Get-Descendants([System.Xml.XmlNode] $node, [string] $tagName) {
    return $node.SelectNodes(".//$tagName")
}

$rows = [System.Collections.Generic.List[string]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$fileCount = 0
$dupCount = 0

$skippedModules = [System.Collections.Generic.List[string]]::new()

foreach ($moduleDir in Get-ChildItem -Path $modulesRoot -Directory) {
    $prefabDir = Join-Path $moduleDir.FullName 'Prefabs'
    if (-not (Test-Path $prefabDir)) { continue }

    if (-not $IncludeAllModules -and $officialModules -notcontains $moduleDir.Name) {
        $skippedModules.Add($moduleDir.Name)
        continue
    }

    foreach ($prefabFile in Get-ChildItem -Path $prefabDir -Filter '*.xml' -File) {
        $fileCount++
        try {
            [xml] $doc = Get-Content -LiteralPath $prefabFile.FullName -Raw
        } catch {
            Write-Warning "Could not parse '$($prefabFile.Name)': $($_.Exception.Message)"
            continue
        }

        # Only TOP-LEVEL game_entity nodes are instantiable prefabs by name. Nested ones are
        # implementation pieces of their parent.
        $tops = $doc.SelectNodes('/prefabs/game_entity')
        if ($null -eq $tops) { continue }

        $category = Get-AssetCategory $prefabFile.BaseName

        foreach ($entity in $tops) {
            $name = [string] $entity.name
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            if (-not $seen.Add($name)) { $dupCount++; continue }

            $physicsNodes = Get-Descendants $entity 'physics'
            $hasPhysics   = if ($physicsNodes.Count -gt 0) { 'yes' } else { '' }

            $shapes    = @($physicsNodes | ForEach-Object { [string] $_.shape })
            $materials = @($physicsNodes | ForEach-Object { [string] $_.material })
            $masses    = @($physicsNodes | ForEach-Object { [string] $_.mass })
            $bodyFlags = @($physicsNodes | ForEach-Object { [string] $_.body_flag })

            $hasCollisionShape =
                if ($shapes | Where-Object { $_ }) { 'yes' }
                elseif ($physicsNodes.Count -gt 0) { 'physics/no-shape-listed' }
                else { '' }

            $meshes = @()
            $meshes += @(Get-Descendants $entity 'meta_mesh_component' | ForEach-Object { [string] $_.name })
            $meshes += @(Get-Descendants $entity 'mesh'                | ForEach-Object { [string] $_.name })

            $scripts = @(Get-Descendants $entity 'script' | ForEach-Object { [string] $_.name })
            $tags    = @(Get-Descendants $entity 'tag'    | ForEach-Object { [string] $_.name })

            $childSections = @(Get-Descendants $entity 'children').Count
            $childTopNames = @($entity.SelectNodes('children/game_entity') | ForEach-Object { [string] $_.name })
            if ($childTopNames.Count -eq 0) { $childTopNames = @($name) }

            $rows.Add((@(
                $name
                $moduleDir.Name
                $prefabFile.Name
                "Prefabs\$($prefabFile.Name)"
                $category
                $hasPhysics
                $hasCollisionShape
                (Join-Values $shapes)
                (Join-Values $materials)
                (Join-Values $masses)
                (Join-Values $bodyFlags)
                (Join-Values $meshes)
                (Join-Values $scripts)
                (Join-Values $tags)
                ([string] $entity.flags)
                ([string] $entity.mobility)
                ([string] $entity.old_prefab_name)
                $childSections
                (Join-Values $childTopNames)
            ) -join ' | '))
        }
    }
}

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$header = @(
    '# Bannerlord Asset Reference Dump'
    "# Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')"
    "# Source install: $GameDir"
    "# Game version: $gameVersion"
    "# Generated by tools/build_asset_dump.ps1 - do not hand-edit, regenerate instead."
    '#'
    '# Column layout matches the legacy bannerlord_assets_v1.3.15.txt exactly: existing bake scripts'
    '# index it positionally (PhysicsShapes = 7, Meshes = 11).'
    '#'
    '# An empty Meshes column means the prefab has no visible geometry. Those are markers and logic'
    '# nodes - spawn points, patrol points, animation points - and are intentionally included.'
    '#'
    '# Official modules only, unless built with -IncludeAllModules. A prefab from a third-party mod'
    '# would be a dead entry for anyone who does not have that mod installed.'
    '#'
    'AssetName | Module | PrefabFile | RelativePath | InferredCategory | HasPhysics | HasCollisionShape | PhysicsShapes | PhysicsMaterials | Masses | BodyFlags | Meshes | Scripts | Tags | Flags | Mobility | OldPrefabName | ChildSectionCount | ChildTopNames'
)

Set-Content -LiteralPath $OutFile -Value ($header + $rows) -Encoding utf8

Write-Host "Wrote $($rows.Count) prefabs from $fileCount prefab files to:"
Write-Host "  $OutFile"
if ($dupCount -gt 0) { Write-Host "Skipped $dupCount duplicate prefab names (first occurrence kept)." }
if ($skippedModules.Count -gt 0) {
    Write-Host "Skipped non-official modules: $($skippedModules -join ', ')"
    Write-Host "  (pass -IncludeAllModules to include them in a local-only dump)"
}
Write-Host ''
Write-Host 'By module:'
$rows | ForEach-Object { ($_ -split ' \| ')[1] } | Group-Object | Sort-Object Count -Descending |
    ForEach-Object { '{0,-22} {1,5}' -f $_.Name, $_.Count }
Write-Host ''
Write-Host 'By inferred category:'
$rows | ForEach-Object { ($_ -split ' \| ')[4] } | Group-Object | Sort-Object Count -Descending |
    ForEach-Object { '{0,-22} {1,5}' -f $_.Name, $_.Count }
Write-Host ''
$noMesh = @($rows | Where-Object { ($_ -split ' \| ')[11] -eq '' }).Count
Write-Host "Meshless (marker/logic) prefabs: $noMesh"
