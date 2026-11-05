# Shared explicit public source and assembly layout for Asset Store and UPM exports.
function Get-UnityPremiumDocumentationSources([string]$Repository) {
    $root = [IO.Path]::GetFullPath((Join-Path $Repository 'docs/unity-premium'))
    $directory = Get-Item -LiteralPath $root
    $excluded = [IO.FileAttributes]::Hidden -bor [IO.FileAttributes]::System -bor [IO.FileAttributes]::ReparsePoint
    if (!$directory.PSIsContainer -or ($directory.Attributes -band $excluded)) { throw 'Premium documentation must be a normal public source directory.' }
    $sources = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        # Only Markdown and reviewed PNG figures belong in
        # offline guides. Hidden files, scripts, credentials, editor/cache files and runtime data
        # cannot enter an archive merely because they happen to be under the documentation folder.
        if ($relative -cnotmatch '^(?:[a-z0-9][a-z0-9-]*/)*[a-z0-9][a-z0-9-]*\.(?:md|png)$') { continue }
        $current = $file; $skip = $false
        while ($current.FullName -ne $root) {
            if ($current.Attributes -band $excluded) { $skip = $true; break }
            $current = if ($current -is [IO.FileInfo]) { $current.Directory } else { $current.Parent }
            if ($null -eq $current) { throw 'Premium documentation source escaped its root.' }
        }
        if ($skip) { continue }
        if (!$file.FullName.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Premium documentation path escaped its root.' }
        if ($file.Length -gt 16777216) { throw "Oversized public documentation asset: $relative" }
        $sources[$relative] = $file.FullName
    }
    if (!$sources.Contains('index.md') -or !$sources.Contains('installation.md')) { throw 'Premium offline documentation is incomplete.' }
    return $sources
}

function Get-UnityPremiumDocumentationEntrypoints([string]$Repository) {
    $link = "`n`n## Complete Premium manual`n`nOpen the [offline Premium task guides](Premium/index.md) for installation, single-player conversion, physics, gameplay values, accounts, chat, voice, relays and troubleshooting.`n"
    return [ordered]@{
        'README.md' = "# SocketJack Premium offline documentation`n`nStart with the [complete Premium guide](Premium/index.md), or open the [quick start](QuickStart.md) and [release-two notes](ReleaseTwo.md). The Premium guide's relative links include its actual Unity and website screenshots.`n"
        'QuickStart.md' = [IO.File]::ReadAllText((Join-Path $Repository 'docs/Unity-AssetStore-QuickStart.md')) + $link
        'ReleaseTwo.md' = [IO.File]::ReadAllText((Join-Path $Repository 'docs/Unity-ReleaseTwo.md')).Replace('(unity-premium/', '(Premium/') + $link
    }
}

function Get-UnityExpansionSources([string]$Repository, [string]$RuntimeDirectory, [string]$RuntimeManifest) {
    $expected = @('Concentus.dll', 'Microsoft.Bcl.AsyncInterfaces.dll', 'SocketJack.Unity.dll', 'SocketJack.Unity.Web.Client.dll', 'SocketJack.Unity.Web.dll', 'SocketJack.Unity.Voice.dll', 'SocketJack.Unity.Voice.Web.Client.dll', 'SocketJack.Unity.Voice.Web.dll', 'System.IO.Pipelines.dll', 'System.Runtime.CompilerServices.Unsafe.dll', 'System.Text.Encodings.Web.dll', 'System.Text.Json.dll')
    $manifest = Get-Content -LiteralPath $RuntimeManifest -Raw | ConvertFrom-Json
    if ($manifest.format -ne 1 -or @(Compare-Object $expected @($manifest.files.name)).Count) { throw 'Expansion DLL manifest differs from the explicit allowlist.' }
    $sources = [ordered]@{}
    foreach ($entry in $manifest.files) {
        if ($entry.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Missing reviewed dependency hash.' }
        $path = Join-Path $RuntimeDirectory $entry.name
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256 -or (Get-Item -LiteralPath $path).Length -ne $entry.bytes) { throw "Expansion dependency differs from its receipt: $($entry.name)" }
        if ($entry.name -eq 'SocketJack.Unity.dll') { continue }
        $sources['Optional/Plugins/' + $entry.name] = $path
    }
    $sourceMap = [ordered]@{
        'Optional/Web/Client/SocketJackWebClientBehaviour.cs' = 'SocketJack.Unity.Web/Unity/SocketJackWebClientBehaviour.cs'
        'Optional/Web/Client/SocketJackWebSessionControls.cs' = 'SocketJack.Unity.Web/Unity/SocketJackWebSessionControls.cs'
        'Optional/Web/Client/SocketJackBrowserApiSocket.cs' = 'SocketJack.Unity.Web/Unity/SocketJackBrowserApiSocket.cs'
        'Optional/Web/Client/Plugins/SocketJackWebApi.jslib' = 'SocketJack.Unity.Web/Unity/SocketJackWebApi.jslib'
        'Optional/Web/Client/SocketJackChatPreset.cs' = 'SocketJack.Unity.Web/Unity/SocketJackChatPreset.cs'
        'Optional/Web/Client/SocketJackMMOChatPanel.cs' = 'SocketJack.Unity.Web/Unity/SocketJackMMOChatPanel.cs'
        'Optional/Web/Client/SocketJackSocialPanel.cs' = 'SocketJack.Unity.Web/Unity/SocketJackSocialPanel.cs'
        'Optional/Web/NativeHost/SocketJackWebHost.cs' = 'SocketJack.Unity.Web/Unity/SocketJackWebHost.cs'
        'Optional/Web/NativeHost/Editor/SocketJackWebHostEditor.cs' = 'SocketJack.Unity.Web/Unity/Editor/SocketJackWebHostEditor.cs'
        'Optional/Web/link.xml' = 'SocketJack.Unity.Web/Unity/link.xml'
        'Optional/Voice/SocketJackVoiceManager.cs' = 'SocketJack.Unity.Voice/Unity/SocketJackVoiceManager.cs'
        'Optional/Voice/SocketJackVoiceControls.cs' = 'SocketJack.Unity.Voice/Unity/SocketJackVoiceControls.cs'
        'Optional/Voice/SocketJackBrowserMicrophone.cs' = 'SocketJack.Unity.Voice/Unity/SocketJackBrowserMicrophone.cs'
        'Optional/Voice/Plugins/WebGL/SocketJackMicrophone.jslib' = 'SocketJack.Unity.Voice/Plugins/WebGL/SocketJackMicrophone.jslib'
        'Optional/VoiceWeb/Client/SocketJackWebVoiceClient.cs' = 'SocketJack.Unity.Voice.Web.Client/Unity/SocketJackWebVoiceClient.cs'
        'Optional/VoiceWeb/Client/SocketJackBrowserVoiceSocket.cs' = 'SocketJack.Unity.Voice.Web.Client/Unity/SocketJackBrowserVoiceSocket.cs'
        'Optional/VoiceWeb/Client/Plugins/WebGL/SocketJackVoiceSocket.jslib' = 'SocketJack.Unity.Voice.Web.Client/Plugins/WebGL/SocketJackVoiceSocket.jslib'
        'Optional/VoiceWeb/NativeHost/SocketJackWebVoiceHost.cs' = 'SocketJack.Unity.Voice.Web/Unity/SocketJackWebVoiceHost.cs'
    }
    foreach ($destination in $sourceMap.Keys) {
        $path = Join-Path $Repository $sourceMap[$destination]
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required expansion source is missing: $($sourceMap[$destination])" }
        $sources[$destination] = $path
    }
    return $sources
}
function Get-UnityAssemblyDefinitions([bool]$IncludeExpansion) {
    $definitions = [ordered]@{}
    function New-Definition([string]$Name, [string[]]$References, [string[]]$Include, [string[]]$Exclude) {
        return ([ordered]@{ name = $Name; rootNamespace = $Name; references = @($References); includePlatforms = @($Include); excludePlatforms = @($Exclude); allowUnsafeCode = $false; overrideReferences = $false; precompiledReferences = @(); autoReferenced = $true; defineConstraints = @(); versionDefines = @(); noEngineReferences = $false } | ConvertTo-Json -Depth 5)
    }
    $definitions['Runtime/SocketJack.Multiplayer.Components.asmdef'] = New-Definition 'SocketJack.Multiplayer.Components' @() @() @()
    $definitions['Runtime/Components/Editor/SocketJack.Multiplayer.Editor.asmdef'] = New-Definition 'SocketJack.Multiplayer.Editor' @('SocketJack.Multiplayer.Components') @('Editor') @()
    $definitions['Samples/Physics/Scripts/SocketJack.Samples.Physics.asmdef'] = New-Definition 'SocketJack.Samples.Physics' @('SocketJack.Multiplayer.Components') @() @()
    $definitions['Samples/Physics/Editor/SocketJack.Samples.Physics.Editor.asmdef'] = New-Definition 'SocketJack.Samples.Physics.Editor' @('SocketJack.Multiplayer.Components', 'SocketJack.Samples.Physics') @('Editor') @()
    if ($IncludeExpansion) {
        $nativePlatforms = @('Editor', 'WindowsStandalone32', 'WindowsStandalone64', 'LinuxStandalone64')
        $definitions['Optional/Web/Client/SocketJack.Multiplayer.Web.Components.asmdef'] = New-Definition 'SocketJack.Multiplayer.Web.Components' @('SocketJack.Multiplayer.Components') @() @()
        $definitions['Optional/Web/NativeHost/SocketJack.Multiplayer.Web.NativeHost.asmdef'] = New-Definition 'SocketJack.Multiplayer.Web.NativeHost' @('SocketJack.Multiplayer.Components', 'SocketJack.Multiplayer.Web.Components') $nativePlatforms @()
        $definitions['Optional/Web/NativeHost/Editor/SocketJack.Multiplayer.Web.Editor.asmdef'] = New-Definition 'SocketJack.Multiplayer.Web.Editor' @('SocketJack.Multiplayer.Web.NativeHost') @('Editor') @()
        $definitions['Optional/Voice/SocketJack.Multiplayer.Voice.Components.asmdef'] = New-Definition 'SocketJack.Multiplayer.Voice.Components' @('SocketJack.Multiplayer.Components') @() @()
        $definitions['Optional/VoiceWeb/Client/SocketJack.Multiplayer.Voice.Web.Components.asmdef'] = New-Definition 'SocketJack.Multiplayer.Voice.Web.Components' @('SocketJack.Multiplayer.Components', 'SocketJack.Multiplayer.Web.Components', 'SocketJack.Multiplayer.Voice.Components') @() @()
        $definitions['Optional/VoiceWeb/NativeHost/SocketJack.Multiplayer.Voice.Web.NativeHost.asmdef'] = New-Definition 'SocketJack.Multiplayer.Voice.Web.NativeHost' @('SocketJack.Multiplayer.Components', 'SocketJack.Multiplayer.Web.NativeHost', 'SocketJack.Multiplayer.Web.Components', 'SocketJack.Multiplayer.Voice.Components') $nativePlatforms @()
        $definitions['Samples/Multiplayer/Scripts/SocketJack.Samples.Multiplayer.asmdef'] = New-Definition 'SocketJack.Samples.Multiplayer' @('SocketJack.Multiplayer.Components', 'SocketJack.Multiplayer.Web.Components', 'SocketJack.Multiplayer.Web.NativeHost', 'SocketJack.Multiplayer.Voice.Components', 'SocketJack.Multiplayer.Voice.Web.Components', 'SocketJack.Multiplayer.Voice.Web.NativeHost') $nativePlatforms @()
        $definitions['Samples/Multiplayer/Editor/SocketJack.Samples.Multiplayer.Editor.asmdef'] = New-Definition 'SocketJack.Samples.Multiplayer.Editor' @('SocketJack.Samples.Multiplayer') @('Editor') @()
    }
    return $definitions
}
function Get-UnityPluginMetadata([string]$Guid, [bool]$NativeHostOnly) {
    # Server/database assemblies stay out of browser players. The Editor can inspect native-host components.
    $web = if ($NativeHostOnly) { 0 } else { 1 }
    $any = if ($NativeHostOnly) { 0 } else { 1 }
    $metadata = @"
fileFormatVersion: 2
guid: $Guid
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: $any
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
        OS: AnyOS
  - first:
      Standalone: Win
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  - first:
      Standalone: Linux64
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  - first:
      WebGL: WebGL
    second:
      enabled: $web
      settings: {}
  userData:
  assetBundleName:
  assetBundleVariant:
"@
    return $metadata + "`n"
}
