#requires -Version 7.4
param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use an owned staging directory outside the checkout.' }
if (Test-Path -LiteralPath (Join-Path $output 'runtime')) { throw 'An existing runtime bundle is retained. Choose a new owned directory.' }
if ((Get-PSDrive -Name ([IO.Path]::GetPathRoot($output).Substring(0, 1))).Free -lt 256MB) { throw 'Reserve 256 MiB for the managed bundle.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
dotnet build (Join-Path $repository 'SocketJack.Unity.Voice.Web/SocketJack.Unity.Voice.Web.csproj') -c Release -p:CopyLocalLockFileAssemblies=true -p:DebugType=none -p:DebugSymbols=false --artifacts-path (Join-Path $output 'artifacts')
if ($LASTEXITCODE -ne 0) { throw 'The complete module graph failed to build.' }
$source = Join-Path $output 'artifacts/bin/SocketJack.Unity.Voice.Web/release'
$expected = @('Concentus.dll', 'Microsoft.Bcl.AsyncInterfaces.dll', 'SocketJack.Unity.dll', 'SocketJack.Unity.Web.Client.dll', 'SocketJack.Unity.Web.dll', 'SocketJack.Unity.Voice.dll', 'SocketJack.Unity.Voice.Web.Client.dll', 'SocketJack.Unity.Voice.Web.dll', 'System.IO.Pipelines.dll', 'System.Runtime.CompilerServices.Unsafe.dll', 'System.Text.Encodings.Web.dll', 'System.Text.Json.dll')
$actual = @(Get-ChildItem -LiteralPath $source -Filter '*.dll' -File | Select-Object -ExpandProperty Name)
# Concentus references the NuGet Vectors facade. Unity already supplies it in its .NET profile;
# importing another copy would duplicate engine/framework types. NuGet consumers resolve it normally.
if (@(Compare-Object ($expected + @('System.Numerics.Vectors.dll')) $actual).Count) { throw 'The runtime dependency graph differs from the explicit managed DLL allowlist.' }
$runtime = Join-Path $output 'runtime'
New-Item -ItemType Directory -Path $runtime | Out-Null
$files = foreach ($name in $expected) {
    $path = Join-Path $source $name
    Copy-Item -LiteralPath $path -Destination (Join-Path $runtime $name)
    [ordered]@{ name = $name; bytes = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[ordered]@{ format = 1; result = 'explicit-managed-runtime-bundle'; files = @($files) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'runtime-manifest.json') -Encoding utf8NoBOM
@("Owned build outputs: $output", 'Purpose: exact managed runtime inputs for Unity Asset Store and UPM exports.', 'Retain release inputs and receipts. No cleanup deletion is authorized or performed.', 'No native SQLite, NAudio, installer, model, account database or live configuration is copied.') | Set-Content -LiteralPath (Join-Path $output 'ownership-manifest.txt') -Encoding utf8NoBOM
Write-Host "Verified runtime bundle: $runtime"
