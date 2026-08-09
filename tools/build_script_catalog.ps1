<#
.SYNOPSIS
    Builds the script catalog: which scene scripts exist, what variables they take, and how often
    they are actually used.

.DESCRIPTION
    Mines every Modules/*/SceneObj/*/scene.xscene for <script name="..."> blocks and the
    <variable name="..." value="..."/> entries inside them.

    Mining the scenes is not a convenience - it is the only way to get the real list. The managed
    assemblies contain ~113 ScriptComponentBehavior subclasses, but the scripts scene authors
    actually use most are engine-side and appear in NO managed type: AnimationPoint, VolumeBox,
    UsablePlace, barrier_builder, mesh_bender. Reflecting over the C# types alone would miss the
    single most-used script in the game.

    Usage counts matter as much as the names. With a few hundred distinct scripts, ordering the
    editor's list by how often shipped scenes use something is the difference between a usable menu
    and an alphabetical wall.

    Variable TYPES are inferred from the observed values, since there is no schema to read: a
    variable that is only ever "true"/"false" is a bool, one that always parses as a number is a
    float, one that always looks like {GUID} is a reference to another entity - and that last case is
    the one that matters, because AnimationPoint's PairEntity is how two posed NPCs are linked.

.PARAMETER GameDir
    Bannerlord install root.

.PARAMETER OutFile
    Where to write the catalog. Defaults to the module's ModuleData folder.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/build_script_catalog.ps1
#>
[CmdletBinding()]
param(
    [string] $GameDir = 'F:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [string] $OutFile = ''
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
if ([string]::IsNullOrEmpty($OutFile)) {
    $OutFile = Join-Path (Resolve-ModuleDataDir $scriptDir $GameDir) 'script_catalog.xml'
}

$modulesRoot = Join-Path $GameDir 'Modules'
if (-not (Test-Path $modulesRoot)) {
    throw "Modules folder not found at '$modulesRoot'. Pass -GameDir with your install path."
}

# Index scanning rather than one big regex. A regex with a lazy body group across a 450KB document
# backtracks catastrophically - the first version of this script ran for over ten minutes on the 600
# scenes and produced nothing. Finding each <script by index and slicing to its own </script> is
# linear and finishes in seconds.
$variableRegex = [regex] '<variable name="([^"]+)" value="([^"]*)"\s*/>'
$guidRegex     = [regex] '^\{[0-9A-Fa-f\-]{36}\}$'

# name -> @{ Count; Vars = @{ varName -> @{ Count; Samples; Values = @{ value -> @{ Uses; Scenes } } } } }
$scripts = @{}
$sceneCount = 0

# How many distinct values to offer as presets for a string variable, and the minimum number of
# distinct scenes a value must appear in to count as one.
#
# The scene-count floor is what separates a shared constant from a scene-local name. Every distinct
# value across every variable is 27,000 of them and 2MB - but most of that is GUIDs and one-off
# path names like "barrier_path_17" that mean nothing in a different scene. Requiring two scenes
# leaves ~1,400 values across 184 variables, which is the set that is actually worth offering.
$maxPresets = 200
$minScenesForPreset = 2

foreach ($moduleDir in Get-ChildItem -Path $modulesRoot -Directory) {
    $sceneObj = Join-Path $moduleDir.FullName 'SceneObj'
    if (-not (Test-Path $sceneObj)) { continue }

    foreach ($sceneDir in Get-ChildItem -Path $sceneObj -Directory) {
        $xscene = Join-Path $sceneDir.FullName 'scene.xscene'
        if (-not (Test-Path $xscene)) { continue }

        $sceneCount++
        $text = [System.IO.File]::ReadAllText($xscene)

        $pos = 0
        while ($true) {
            $start = $text.IndexOf('<script name="', $pos, [StringComparison]::Ordinal)
            if ($start -lt 0) { break }

            $nameStart = $start + 14
            $nameEnd = $text.IndexOf('"', $nameStart)
            if ($nameEnd -lt 0) { break }
            $name = $text.Substring($nameStart, $nameEnd - $nameStart)

            # Self-closing is decided by THIS tag's own end, not by the next '/>' in the document -
            # for a script with variables the next '/>' belongs to a <variable>, which made every
            # script look self-closing and reported zero variables for all 130 of them.
            $tagEnd = $text.IndexOf('>', $nameEnd)
            if ($tagEnd -lt 0) { break }

            $body = ''
            if ($text[$tagEnd - 1] -eq '/') {
                $pos = $tagEnd + 1
            } else {
                $close = $text.IndexOf('</script>', $tagEnd, [StringComparison]::Ordinal)
                if ($close -lt 0) { $pos = $tagEnd + 1 }
                else {
                    $body = $text.Substring($tagEnd + 1, $close - $tagEnd - 1)
                    $pos = $close + 9
                }
            }

            if (-not $scripts.ContainsKey($name)) {
                $scripts[$name] = @{ Count = 0; Vars = @{} }
            }
            $entry = $scripts[$name]
            $entry.Count++

            if ($body.Length -eq 0) { continue }

            foreach ($varMatch in $variableRegex.Matches($body)) {
                $varName  = $varMatch.Groups[1].Value
                $varValue = $varMatch.Groups[2].Value

                if (-not $entry.Vars.ContainsKey($varName)) {
                    $entry.Vars[$varName] = @{
                        Count   = 0
                        Samples = [System.Collections.Generic.List[string]]::new()
                        Values  = @{}
                    }
                }
                $var = $entry.Vars[$varName]
                $var.Count++
                if ($var.Samples.Count -lt 8 -and -not $var.Samples.Contains($varValue)) {
                    $var.Samples.Add($varValue) | Out-Null
                }

                if (-not $var.Values.ContainsKey($varValue)) {
                    $var.Values[$varValue] = @{ Uses = 0; Scenes = [System.Collections.Generic.HashSet[string]]::new() }
                }
                $var.Values[$varValue].Uses++
                $var.Values[$varValue].Scenes.Add($sceneDir.Name) | Out-Null
            }
        }
    }
}

function Get-VariableType($samples) {
    $values = @($samples | Where-Object { $_ -ne $null })
    if ($values.Count -eq 0) { return 'string' }

    $allBool = $true; $allNumber = $true; $allGuid = $true
    foreach ($v in $values) {
        if ($v -ne 'true' -and $v -ne 'false') { $allBool = $false }
        $parsed = 0.0
        if (-not [double]::TryParse($v, [ref] $parsed)) { $allNumber = $false }
        if (-not $guidRegex.IsMatch($v)) { $allGuid = $false }
    }

    # Entity references first: a GUID also fails the number test, and confusing the two would turn a
    # link between two entities into a text box.
    if ($allGuid)   { return 'entity' }
    if ($allBool)   { return 'bool' }
    if ($allNumber) { return 'float' }
    return 'string'
}

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)

$writer = [System.Xml.XmlWriter]::Create((Resolve-Path -LiteralPath $outDir).Path + '\' + (Split-Path -Leaf $OutFile), $settings)
try {
    $writer.WriteStartDocument()
    $writer.WriteComment(" Generated by tools/build_script_catalog.ps1 on $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ")
    $writer.WriteComment(" Mined from $sceneCount scene.xscene files under $GameDir ")
    $writer.WriteComment(" Uses = how many times shipped scenes attach this script. Types are inferred from observed values. ")
    $writer.WriteStartElement('ScriptCatalog')

    foreach ($name in ($scripts.Keys | Sort-Object { -$scripts[$_].Count })) {
        $entry = $scripts[$name]
        $writer.WriteStartElement('Script')
        $writer.WriteAttributeString('name', $name)
        $writer.WriteAttributeString('uses', [string] $entry.Count)

        foreach ($varName in ($entry.Vars.Keys | Sort-Object { -$entry.Vars[$_].Count })) {
            $var = $entry.Vars[$varName]
            $varType = Get-VariableType $var.Samples

            $writer.WriteStartElement('Variable')
            $writer.WriteAttributeString('name', $varName)
            $writer.WriteAttributeString('type', $varType)
            $writer.WriteAttributeString('uses', [string] $var.Count)
            if ($var.Samples.Count -gt 0) {
                $writer.WriteAttributeString('default', $var.Samples[0])
                $writer.WriteAttributeString('samples', ($var.Samples -join ' | '))
            }

            # Presets, for string variables only. A float or a bool has nothing to list, and an
            # entity reference is a per-scene GUID that would be meaningless anywhere else.
            if ($varType -eq 'string') {
                $presets = $var.Values.Keys |
                    Where-Object { $_.Trim().Length -gt 0 -and $var.Values[$_].Scenes.Count -ge $minScenesForPreset } |
                    Sort-Object { -$var.Values[$_].Uses } |
                    Select-Object -First $maxPresets

                foreach ($presetValue in $presets) {
                    $writer.WriteStartElement('Value')
                    $writer.WriteAttributeString('text', $presetValue)
                    $writer.WriteAttributeString('uses', [string] $var.Values[$presetValue].Uses)
                    $writer.WriteAttributeString('scenes', [string] $var.Values[$presetValue].Scenes.Count)
                    $writer.WriteEndElement()
                }
            }

            $writer.WriteEndElement()
        }

        $writer.WriteEndElement()
    }

    $writer.WriteEndElement()
    $writer.WriteEndDocument()
} finally {
    $writer.Flush(); $writer.Close()
}

Write-Host "Scanned $sceneCount scenes; found $($scripts.Count) distinct scripts."
Write-Host "Wrote $OutFile"
Write-Host ''
Write-Host 'Most used:'
$scripts.Keys | Sort-Object { -$scripts[$_].Count } | Select-Object -First 25 | ForEach-Object {
    '{0,-34} {1,7}  ({2} variable(s))' -f $_, $scripts[$_].Count, $scripts[$_].Vars.Count
}
