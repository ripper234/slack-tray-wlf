#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot

if ($env:OS -ne 'Windows_NT') {
    throw 'Run these tests on Windows. Registry fixtures use a private HKCU subtree.'
}

# Parse all scripts before running code, with Windows PowerShell's own parser.
$scripts = Get-ChildItem -LiteralPath $projectRoot -Filter '*.ps1' -Recurse -File
foreach ($script in $scripts) {
    $tokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile(
        $script.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw ('PowerShell parse errors in {0}: {1}' -f $script.FullName, ($parseErrors | Out-String))
    }
}
Write-Host ('Parsed {0} PowerShell scripts.' -f $scripts.Count)

$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'The Windows .NET Framework C# compiler was not found.' }

$testDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('SlackTrayHours.Tests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testDirectory
try {
    $testExe = Join-Path $testDirectory 'PolicyAndRegistryTests.exe'
    $compilerArgs = @(
        '/nologo', '/target:exe', '/optimize+', '/warn:4',
        '/main:SlackTrayHours.Tests.PolicyAndRegistryTests',
        ('/out:' + $testExe),
        (Join-Path $projectRoot 'src\SlackTrayHours.cs'),
        (Join-Path $PSScriptRoot 'PolicyAndRegistryTests.cs')
    )
    & $compiler @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw ('Test compilation failed: ' + $LASTEXITCODE) }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw ('Tests failed: ' + $LASTEXITCODE) }
} finally {
    Remove-Item -LiteralPath $testDirectory -Recurse -Force
}
