<#
.SYNOPSIS
    Runs a hands-off two-player OmniBonk session on this PC: an auto-host copy and a BotMode copy.

.DESCRIPTION
    Debug builds only (the bot and auto-host are compiled out of Release). The script:
      1. builds the plugin in Debug (skip with -NoBuild) and copies it into both installs,
      2. backs up each copy's OmniBonk.cfg and BepInEx.cfg and writes the test settings,
      3. starts the local matchmaking server if nothing is listening on port 5000,
      4. launches both copies windowed, straight from their exe (no Steam needed:
         UseSteamTransport is off), host first,
      5. waits -Minutes, closes both, restores the configs, and copies both logs into
         -OutDir with a short summary.
    The host opens a lobby, the bot joins through the room-code file, both ready up, the host
    starts, picks the map, wanders, and takes the portal every -StageMinutes. See
    docs/netplay/05-local-testing.md, "Hands-off session".

.EXAMPLE
    pwsh scripts/test/run-local-session.ps1 -Minutes 12 -StageMinutes 4 -ClientItems "SoulHarvester, EnergyCore"
#>
[CmdletBinding()]
param(
    [string] $HostPath = "C:\Program Files (x86)\Steam\steamapps\common\Megabonk",
    [string] $ClientPath = "D:\Games\Megabonk",
    [double] $Minutes = 10,
    [double] $StageMinutes = 3,
    [string] $Map = "Desert",
    [string] $HostItems = "",
    [string] $ClientItems = "EnergyCore, SoulHarvester, Ghost",
    [string] $HostWeapons = "",
    [string] $ClientWeapons = "",
    [string] $OutDir = "",
    [switch] $NoBuild,
    [switch] $KeepConfigs
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $OutDir) { $OutDir = Join-Path $repo "artifacts\local-session\$(Get-Date -Format 'yyyyMMdd-HHmmss')" }
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Set-CfgValue([string] $path, [string] $section, [string] $key, [string] $value) {
    $lines = [System.Collections.Generic.List[string]]::new()
    if (Test-Path $path) { $lines.AddRange([string[]](Get-Content $path)) }
    $inSection = $false; $sectionEnd = -1; $sectionFound = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].Trim()
        if ($line -match '^\[(.+)\]$') {
            if ($inSection) { $sectionEnd = $i; break }
            $inSection = ($Matches[1] -eq $section)
            if ($inSection) { $sectionFound = $true }
            continue
        }
        if ($inSection -and $line -match "^$([regex]::Escape($key))\s*=") {
            $lines[$i] = "$key = $value"
            Set-Content -Path $path -Value $lines -Encoding utf8NoBOM
            return
        }
    }
    if (-not $sectionFound) {
        $lines.Add(""); $lines.Add("[$section]"); $lines.Add("$key = $value")
    } elseif ($sectionEnd -ge 0) {
        $lines.Insert($sectionEnd, "$key = $value")
    } else {
        $lines.Add("$key = $value")
    }
    Set-Content -Path $path -Value $lines -Encoding utf8NoBOM
}

function Get-Copies {
    @(
        @{ Name = "host";   Path = $HostPath;   Player = "AutoHost"; Items = $HostItems;   Weapons = $HostWeapons;   AutoHost = "true" },
        @{ Name = "client"; Path = $ClientPath; Player = "Bot";      Items = $ClientItems; Weapons = $ClientWeapons; AutoHost = "false" }
    )
}

# A run killed before its finally block leaves its backups behind: put the player's configs back first.
foreach ($copy in Get-Copies) {
    Get-ChildItem (Join-Path $copy.Path "BepInEx\config") -Filter "*.session-backup" -ErrorAction SilentlyContinue | ForEach-Object {
        Move-Item $_.FullName ($_.FullName -replace '\.session-backup$', '') -Force
        Write-Warning "Restored $($_.Name) left by an interrupted run."
    }
}

# 1. Build and deploy.
if (-not $NoBuild) {
    Write-Host "Building Debug..."
    dotnet build (Join-Path $repo "src\plugin\OmniBonk.Plugin.csproj") -c Debug -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}
$buildOut = Join-Path $repo "src\plugin\bin\Debug\net6.0"
$builtDll = Join-Path $buildOut "OmniBonk.dll"
if (-not (Test-Path $builtDll)) { throw "No Debug build at $buildOut." }
$builtHash = (Get-FileHash $builtDll).Hash

foreach ($copy in Get-Copies) {
    if (Get-Process Megabonk -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$($copy.Path)*" }) {
        throw "Megabonk is already running from $($copy.Path). Close it first."
    }
    # OmniBonk refuses to load beside the old plugin ([BepInIncompatibility]); a pre-rename build may have put it back.
    $old = Get-ChildItem (Join-Path $copy.Path "BepInEx\plugins") -Recurse -Filter "MegabonkTogether.dll" -ErrorAction SilentlyContinue
    if ($old) { throw "Old MegabonkTogether plugin at $($old[0].DirectoryName): OmniBonk will not load beside it. Move it out of plugins." }
    $plugins = Join-Path $copy.Path "BepInEx\plugins\OmniBonk"
    New-Item -ItemType Directory -Force $plugins | Out-Null
    Get-ChildItem $buildOut -File | Copy-Item -Destination $plugins -Force
    if ((Get-FileHash (Join-Path $plugins "OmniBonk.dll")).Hash -ne $builtHash) { throw "Copy to $plugins did not take." }
}
Write-Host "Deployed build $($builtHash.Substring(0, 12)) to both copies."

# 2. Back up and write the test settings.
$backups = @()
foreach ($copy in Get-Copies) {
    foreach ($file in "OmniBonk.cfg", "BepInEx.cfg") {
        $path = Join-Path $copy.Path "BepInEx\config\$file"
        if (Test-Path $path) {
            $backup = "$path.session-backup"
            Copy-Item $path $backup -Force
            $backups += @{ Path = $path; Backup = $backup }
        }
    }
    $cfg = Join-Path $copy.Path "BepInEx\config\OmniBonk.cfg"
    Set-CfgValue $cfg "Bot" "BotMode" "true"
    Set-CfgValue $cfg "Bot" "BotInvincible" "true"
    Set-CfgValue $cfg "Bot" "AutoHost" $copy.AutoHost
    Set-CfgValue $cfg "Bot" "AutoHostPlayers" "2"
    Set-CfgValue $cfg "Bot" "AutoHostStageMinutes" ([string]$StageMinutes)
    Set-CfgValue $cfg "Bot" "AutoHostMap" $Map
    Set-CfgValue $cfg "Testing" "TestItems" $copy.Items
    Set-CfgValue $cfg "Testing" "TestWeapons" $copy.Weapons
    Set-CfgValue $cfg "Player" "PlayerName" $copy.Player
    Set-CfgValue $cfg "Network" "ServerUrl" "ws://127.0.0.1:5000"
    Set-CfgValue $cfg "Network" "UseSteamTransport" "false"
    Set-CfgValue $cfg "Updates" "CheckForUpdates" "false"
    Set-CfgValue $cfg "Updates" "ShowChangelog" "false"
    # Endless starts in crypt rooms, which have no boss portal for the auto-host to take.
    Set-CfgValue $cfg "Lobby" "EndlessRun" "false"
    # Both copies share one Megabonk save folder; a test session must not write it.
    Set-CfgValue $cfg "Gameplay" "AllowSavesDuringNetplay" "false"
    Set-CfgValue (Join-Path $copy.Path "BepInEx\config\BepInEx.cfg") "Logging.Disk" "WriteUnityLog" "true"
}

$server = $null
$processes = @()
try {
    # 3. Matchmaking server.
    $listening = Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue
    if (-not $listening) {
        Write-Host "Starting the matchmaking server..."
        $server = Start-Process dotnet -ArgumentList "run", "--project", (Join-Path $repo "src\server\OmniBonk.Server.csproj") `
            -PassThru -WindowStyle Minimized -RedirectStandardOutput (Join-Path $OutDir "server.log") -RedirectStandardError (Join-Path $OutDir "server.err.log")
        $deadline = (Get-Date).AddSeconds(90)
        while (-not (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue)) {
            if ((Get-Date) -gt $deadline) { throw "The matchmaking server did not start listening on 5000." }
            Start-Sleep 2
        }
    }

    # 4. Launch, host first so its room code is written before the bot looks for it.
    $windowArgs = "-screen-fullscreen 0 -screen-width 960 -screen-height 540"
    foreach ($copy in Get-Copies) {
        $exe = Join-Path $copy.Path "Megabonk.exe"
        Write-Host "Launching the $($copy.Name) copy: $exe"
        $processes += Start-Process $exe -ArgumentList $windowArgs -WorkingDirectory $copy.Path -PassThru
        Start-Sleep 20
    }

    # 5. Let it play.
    $end = (Get-Date).AddMinutes($Minutes)
    Write-Host "Session running until $($end.ToString('HH:mm:ss'))."
    while ((Get-Date) -lt $end) {
        if ($processes | Where-Object { $_.HasExited }) { Write-Warning "A copy exited early."; break }
        Start-Sleep 10
    }
}
finally {
    foreach ($p in $processes) {
        if (-not $p.HasExited) { $null = $p.CloseMainWindow() }
    }
    Start-Sleep 15
    foreach ($p in $processes) {
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
    if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }

    if (-not $KeepConfigs) {
        foreach ($b in $backups) { Move-Item $b.Backup $b.Path -Force }
        Write-Host "Restored both copies' configs."
    }

    foreach ($copy in Get-Copies) {
        $log = Join-Path $copy.Path "BepInEx\LogOutput.log"
        if (Test-Path $log) { Copy-Item $log (Join-Path $OutDir "$($copy.Name).LogOutput.log") -Force }
    }

    $pattern = '\[bot\]|\[item-fx\]|\[rocket\]|\[report\]|\[endless\]|Lobby is ready|Exception|\[Error'
    $summary = foreach ($copy in Get-Copies) {
        "=== $($copy.Name)"
        $log = Join-Path $OutDir "$($copy.Name).LogOutput.log"
        if (Test-Path $log) { Select-String -Path $log -Pattern $pattern | ForEach-Object { $_.Line } }
    }
    $summary | Set-Content (Join-Path $OutDir "summary.txt") -Encoding utf8NoBOM
    Write-Host "Logs and summary in $OutDir"
}
