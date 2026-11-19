param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [string]$DotnetExe = "dotnet",
    [string]$NugetSource = "",
    [switch]$SingleFile
)

$projectPath = Join-Path $PSScriptRoot "ApexKernelOptimizer.csproj"
$publishPath = Join-Path $PSScriptRoot "dist\$Runtime"

if (Test-Path $publishPath) {
    Remove-Item $publishPath -Recurse -Force -ErrorAction SilentlyContinue
}

$publishArgs = @(
    "publish", $projectPath,
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:DebuggerSupport=false",
    "-p:EventSourceSupport=false",
    "-p:UseSystemResourceKeys=true",
    "-p:EnableUnsafeBinaryFormatterSerialization=false",
    "-o", $publishPath
)

if ($SingleFile) {
    $publishArgs += @(
        "-p:PublishSingleFile=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true"
    )
}

if (-not [string]::IsNullOrWhiteSpace($NugetSource)) {
    $publishArgs += @("--source", $NugetSource)
}

& $DotnetExe @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "La publicación falló con código $LASTEXITCODE."
}

Get-ChildItem -Path $publishPath -Include *.pdb, *.xml -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

$sizeMb = [Math]::Round(((Get-ChildItem $publishPath -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB), 2)
Write-Host "Aplicación autocontenida creada en: $publishPath ($sizeMb MB)"
