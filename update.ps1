[CmdletBinding()]
param([switch]$PauseAtEnd)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$installDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SlackTrayHours'
$stage = $null
$success = $false

function Convert-StrictVersion([string]$Value, [string]$Location) {
    if ($value -cnotmatch '\A[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}\r?\n?\z') {
        throw "Version must contain only three numeric components, for example 0.3.0: $Location"
    }
    return [Version]::Parse($value.Trim())
}

function Read-StrictVersion([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Version file is missing: $Path" }
    return Convert-StrictVersion ([IO.File]::ReadAllText($Path)) $Path
}

function Expand-CheckedArchive([string]$ZipPath, [string]$Destination, [string]$CommitSha) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $required = @('VERSION', 'install.ps1', 'Build.ps1', 'src/SlackTrayHours.cs',
                  'uninstall.ps1', 'Uninstall.cmd', 'status.ps1', 'Status.cmd', 'Update.cmd', 'update.ps1')
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        if ($zip.Entries.Count -lt $required.Count -or $zip.Entries.Count -gt 2000) {
            throw 'The downloaded archive has an unexpected number of files.'
        }
        $rootMatch = [regex]::Match($zip.Entries[0].FullName, '\A(slack-tray-wlf-([0-9a-f]{7,40}))/', [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if (-not $rootMatch.Success -or
            -not $CommitSha.StartsWith($rootMatch.Groups[2].Value, [StringComparison]::Ordinal)) {
            throw 'The downloaded archive has an unexpected root folder.'
        }
        $rootName = $rootMatch.Groups[1].Value
        $prefix = "$rootName/"
        $sourceRoot = Join-Path $Destination $rootName
        $sourceRootPrefix = [IO.Path]::GetFullPath($sourceRoot) + [IO.Path]::DirectorySeparatorChar
        $totalSize = [long]0
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if (-not $name.StartsWith($prefix, [StringComparison]::Ordinal)) {
                throw 'The downloaded archive has an unexpected root folder.'
            }
            $relative = $name.Substring($prefix.Length)
            if ($relative -eq '') { continue } # The root directory entry is optional.
            if ($relative -cmatch '[\\:<>"|?*\x00-\x1F]' -or $relative -cmatch '//') {
                throw "The downloaded archive contains an unsafe path: $name"
            }
            $isDirectory = $relative.EndsWith('/')
            $components = $relative.TrimEnd('/').Split('/')
            if (@($components | Where-Object {
                $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') -or $_.EndsWith(' ') -or
                $_ -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)'
            }).Count -ne 0) {
                throw "The downloaded archive contains an unsafe path: $name"
            }
            $key = $relative.TrimEnd('/')
            if (-not $seen.Add($key)) { throw "The downloaded archive repeats a path: $name" }
            $fileType = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($fileType -eq 0xA000) { throw "The downloaded archive contains a symbolic link: $name" }
            if ($isDirectory -and $entry.Length -ne 0) { throw "The downloaded archive contains an invalid directory: $name" }
            $totalSize += $entry.Length
            if ($totalSize -gt 268435456) { throw 'The downloaded archive is unexpectedly large.' }
            $destinationPath = [IO.Path]::GetFullPath((Join-Path $sourceRoot ($relative.Replace([char]'/', [char][IO.Path]::DirectorySeparatorChar))))
            if (-not $destinationPath.StartsWith($sourceRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "The downloaded archive contains an unsafe path: $name"
            }
        }
        foreach ($file in $required) {
            if (-not $seen.Contains($file)) { throw "The downloaded archive is missing $file." }
        }
        $null = New-Item -ItemType Directory -Path $sourceRoot -Force
        $written = [long]0
        foreach ($entry in $zip.Entries) {
            $relative = $entry.FullName.Substring($prefix.Length)
            if ($relative -eq '') { continue }
            $destinationPath = [IO.Path]::GetFullPath((Join-Path $sourceRoot ($relative.Replace([char]'/', [char][IO.Path]::DirectorySeparatorChar))))
            if ($relative.EndsWith('/')) {
                $null = New-Item -ItemType Directory -Path $destinationPath -Force
                continue
            }
            $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destinationPath)) -Force
            $inputStream = $entry.Open()
            try {
                $outputStream = [IO.File]::Create($destinationPath)
                try {
                    $buffer = New-Object byte[] 65536
                    $entryWritten = [long]0
                    while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $entryWritten += $count
                        $written += $count
                        if ($entryWritten -gt $entry.Length -or $written -gt 268435456) {
                            throw 'The downloaded archive expanded beyond its stated size.'
                        }
                        $outputStream.Write($buffer, 0, $count)
                    }
                    if ($entryWritten -ne $entry.Length) { throw "Incomplete archive entry: $($entry.FullName)" }
                }
                finally { $outputStream.Dispose() }
            }
            finally { $inputStream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    return $sourceRoot
}

try {
    $current = Read-StrictVersion (Join-Path $installDirectory 'VERSION')
    if (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'installation.json') -PathType Leaf)) {
        throw 'No installed Slack Tray Hours marker was found. Use Install.cmd from the project download first.'
    }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent' = 'SlackTrayHours-Updater'; 'Accept' = 'application/vnd.github+json' }
    Write-Host 'Checking the latest commit on GitHub...'
    $commit = Invoke-RestMethod -Uri 'https://api.github.com/repos/ripper234/slack-tray-wlf/commits/main' -Headers $headers -TimeoutSec 30
    $sha = [string]$commit.sha
    if ($sha -cnotmatch '\A[0-9a-f]{40}\z') { throw 'GitHub returned an invalid commit SHA.' }
    $versionUrl = "https://api.github.com/repos/ripper234/slack-tray-wlf/contents/VERSION?ref=$sha"
    $versionResponse = Invoke-RestMethod -Uri $versionUrl -Headers $headers -TimeoutSec 30
    if ($versionResponse.encoding -cne 'base64' -or $versionResponse.content -isnot [string] -or
        $versionResponse.size -gt 64) {
        throw 'GitHub returned an invalid VERSION file.'
    }
    $versionBytes = [Convert]::FromBase64String(($versionResponse.content -replace '\s', ''))
    if ($versionBytes.Length -gt 64) { throw 'GitHub returned an invalid VERSION file.' }
    $utf8 = New-Object Text.UTF8Encoding($false, $true)
    $available = Convert-StrictVersion ($utf8.GetString($versionBytes)) "VERSION at $sha"
    Write-Host "Installed version: $current"
    Write-Host "GitHub version:    $available ($sha)"
    if ($available.CompareTo($current) -le 0) {
        Write-Host 'There is no newer version to install.'
        $success = $true
    }
    else {
        Write-Host 'The update will stop the current background helper, reinstall it, and restart it using your existing schedule.'
        $answer = Read-Host 'Type UPDATE to proceed'
        if ($answer -cne 'UPDATE') {
            Write-Host 'Update cancelled. The current installation was not changed.'
            $success = $true
        }
        else {
            $stage = Join-Path ([IO.Path]::GetTempPath()) ('SlackTrayHours-update-' + [Guid]::NewGuid().ToString('N'))
            $null = New-Item -ItemType Directory -Path $stage
            $archive = Join-Path $stage 'source.zip'
            $extractTo = Join-Path $stage 'source'
            $null = New-Item -ItemType Directory -Path $extractTo
            $url = "https://github.com/ripper234/slack-tray-wlf/archive/$sha.zip"
            Write-Host "Downloading source for commit $sha..."
            Invoke-WebRequest -Uri $url -Headers $headers -OutFile $archive -UseBasicParsing -TimeoutSec 90 | Out-Null
            if ((Get-Item -LiteralPath $archive).Length -gt 104857600) { throw 'The downloaded archive is unexpectedly large.' }
            $sourceRoot = Expand-CheckedArchive $archive $extractTo $sha
            $archiveVersion = Read-StrictVersion (Join-Path $sourceRoot 'VERSION')
            if ($archiveVersion.CompareTo($available) -ne 0) {
                throw "The downloaded archive version $archiveVersion differs from the preflight version $available. Nothing was installed."
            }
            $installer = Join-Path $sourceRoot 'install.ps1'
            & (Join-Path $PSHOME 'powershell.exe') -NoLogo -NoProfile -ExecutionPolicy Bypass -File $installer -KeepSettings
            if ($LASTEXITCODE -ne 0) { throw "The installer failed with exit code $LASTEXITCODE. Read its rollback or recovery message above." }
            $nowInstalled = Read-StrictVersion (Join-Path $installDirectory 'VERSION')
            if ($nowInstalled.CompareTo($available) -ne 0) {
                throw "The installer returned success but the installed version is $nowInstalled instead of $available."
            }
            Write-Host "Updated Slack Tray Hours to $available. Your workweek and hours were retained."
            $success = $true
        }
    }
}
catch { Write-Host "Update failed: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if ($PauseAtEnd) { $null = Read-Host 'Press Enter to close' }
if (-not $success) { exit 1 }
