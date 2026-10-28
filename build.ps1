param(
    [switch]$RunTests,
    [switch]$TestOnly,
    [switch]$RealPayloadTest
)
$ErrorActionPreference = 'Stop'
$distributionRoot = $PSScriptRoot
$sourceRoot = Join-Path $distributionRoot 'src'
$buildRoot = Join-Path $distributionRoot 'build'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.x C# compiler was not found.' }
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$manifestPath = Join-Path $distributionRoot 'payload\manifest.json'
if ($TestOnly) {
    $testFiles = @(
        @{relativePath='settings/hp_settings/string_ids.stringlookup'; sha256=('0' * 64)},
        @{relativePath='ui/font_en.gfx'; sha256=('0' * 64)}
    )
    $dummy = @{patchVersion='1.0.0'; supportedSteamBuild='26051119'; codeFinalVersion='3366163'; archivesVersion='3322479'; githubRepository='fullmetalsonic/thehunter-cotw-korean-ai'; files=$testFiles}
    $manifestPath = Join-Path $buildRoot 'test-manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($dummy | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
} elseif (-not (Test-Path -LiteralPath $manifestPath)) { throw 'payload/manifest.json is required. Use -TestOnly -RunTests for dummy sandbox tests.' }
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll')
$shared = @('/nologo', '/optimize+', '/platform:anycpu', '/utf8output', "/resource:$manifestPath,patch-manifest.json") + $references
$source = Join-Path $sourceRoot 'Patcher.cs'
$packageManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$assemblyVersion = [Version]::Parse(($packageManifest.patchVersion -replace '\+.*$', '') + '.0').ToString()
$assemblyInfo = Join-Path $buildRoot 'VersionInfo.cs'
[IO.File]::WriteAllText($assemblyInfo, "[assembly: System.Reflection.AssemblyVersion(`"$assemblyVersion`")]`n[assembly: System.Reflection.AssemblyFileVersion(`"$assemblyVersion`")]", [Text.UTF8Encoding]::new($false))
if (-not $TestOnly) {
    $applicationManifest = Join-Path $buildRoot 'app.manifest'
    $applicationManifestText = @'
<?xml version="1.0" encoding="utf-8"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity name="HunterAiTranslationPatcher" version="ASSEMBLY_VERSION" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false" /></requestedPrivileges></security></trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings><dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware></windowsSettings></application>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /></application></compatibility>
</assembly>
'@
    [IO.File]::WriteAllText($applicationManifest, $applicationManifestText.Replace('ASSEMBLY_VERSION', $assemblyVersion), [Text.UTF8Encoding]::new($false))
    $executable = Join-Path $buildRoot 'AI 자동 번역 패치.exe'
    & $compiler @shared '/target:winexe' "/out:$executable" "/win32manifest:$applicationManifest" $source $assemblyInfo
    if ($LASTEXITCODE -ne 0) { throw "Patcher compilation failed: $LASTEXITCODE" }
    Write-Output "Built: $executable"
}
if ($RunTests -or $RealPayloadTest) {
    $runner = Join-Path $buildRoot 'patcher-tests.exe'
    & $compiler @shared '/target:exe' '/define:PATCHER_TESTS' '/main:HunterPatcher.TestProgram' "/out:$runner" $source $assemblyInfo (Join-Path $sourceRoot 'PatcherTests.cs')
    if ($LASTEXITCODE -ne 0) { throw "Test compilation failed: $LASTEXITCODE" }
    if ($RunTests) {
        & $runner '--report' (Join-Path $distributionRoot 'patcher-validation.json')
        if ($LASTEXITCODE -ne 0) { throw "Sandbox tests failed: $LASTEXITCODE" }
    }
    if ($RealPayloadTest) {
        if ($TestOnly) { throw '-RealPayloadTest requires the final payload manifest, without -TestOnly.' }
        & $runner '--report' (Join-Path $distributionRoot 'patcher-real-payload-validation.json') '--real-payload-test' (Join-Path $distributionRoot 'payload')
        if ($LASTEXITCODE -ne 0) { throw "Real payload sandbox test failed: $LASTEXITCODE" }
    }
}
