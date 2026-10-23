param([string]$Version = '0.1.1')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Version must be a SemVer package version.' }
$root = $PSScriptRoot
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { $null }
if (-not $dotnet) {
    $candidate = Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $candidate) { $dotnet = $candidate } else { throw '.NET SDK 9 or later is required.' }
}
function Run-Dotnet([string[]]$Arguments) {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Push-Location $root
try {
    Run-Dotnet @('restore', 'Nebula.Seq.sln')
    Run-Dotnet @('build', 'Nebula.Seq.sln', '-c', 'Release', '--no-restore', "-p:Version=$Version")
    Run-Dotnet @('test', 'tests/Nebula.Seq.Tests/Nebula.Seq.Tests.csproj', '-c', 'Release', '--no-build', '--logger', 'trx;LogFileName=tests.trx', '--results-directory', 'artifacts/test-results')
    Run-Dotnet @('pack', 'src/Seq.App.Nebula/Seq.App.Nebula.csproj', '-c', 'Release', '--no-build', '-o', 'artifacts', "-p:Version=$Version")

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = Join-Path $root "artifacts/Seq.App.Nebula.$Version.nupkg"
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName })
        foreach ($required in @('Seq.App.Nebula.dll', 'Nebula.Seq.Core.dll', 'Nebula.Seq.JiraMcp.dll', 'Nebula.Seq.JiraMcp.deps.json', 'Nebula.Seq.JiraMcp.runtimeconfig.json')) {
            if ($entries -notcontains "lib/net8.0/$required") { throw "Package missing $required" }
        }
        if ($entries | Where-Object { $_ -match '(Seq\.Apps|Serilog)\.dll$' }) { throw 'Package includes Seq-owned assemblies.' }
        $manifestEntry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($manifest.SelectNodes("//*[local-name()='dependency']").Count -gt 0) { throw 'Seq app package must bundle dependencies.' }
    }
    finally { $zip.Dispose() }
    # Seq's installer uses an OPC package reader, which is stricter than ZipArchive.
    # Validate with WindowsBase so invalid content-type part URIs fail before upload.
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    if (-not (Test-Path -LiteralPath $windowsPowerShell)) { throw 'Windows PowerShell 5.1 is required for Seq package compatibility validation.' }
    & $windowsPowerShell -NoProfile -NonInteractive -File (Join-Path $root 'scripts/Test-SeqPackage.ps1') -PackagePath $package
    if ($LASTEXITCODE -ne 0) { throw 'Seq package reader compatibility validation failed.' }
    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
    Set-Content -LiteralPath "$package.sha256" -Value "$hash  Seq.App.Nebula.$Version.nupkg" -Encoding ASCII
    Write-Host "Verified package: $package"
    Write-Host "SHA256: $hash"
}
finally { Pop-Location }
