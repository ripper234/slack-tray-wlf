[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot 'src\SlackTrayHours.cs'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'artifacts\SlackTrayHours.exe'),
    [string[]]$AdditionalSources = @(),
    [string]$MainType = 'SlackTrayHours.Program',
    [ValidateSet('winexe', 'exe')][string]$Target = 'winexe'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Use the C# compiler already included in Windows. No downloads or SDK install.
$framework = if ([Environment]::Is64BitOperatingSystem) { 'Framework64' } else { 'Framework' }
$compiler = Join-Path $env:WINDIR "Microsoft.NET\$framework\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The built-in .NET Framework C# compiler was not found. This build requires Windows with .NET Framework 4.x.'
}
$sourceFiles = @($SourcePath) + @($AdditionalSources)
foreach ($source in $sourceFiles) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Source file not found: $source" }
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($OutputPath)) -Force
$arguments = @('/nologo', '/optimize+', '/platform:anycpu', '/warn:4', "/target:$Target", "/out:$OutputPath", '/reference:System.dll', '/reference:System.Core.dll')
if ($MainType) { $arguments += "/main:$MainType" }
$arguments += $sourceFiles
& $compiler @arguments
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
    throw "Compilation failed (compiler exit code $LASTEXITCODE)."
}
Write-Host "Built $OutputPath"
