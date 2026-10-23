param([Parameter(Mandatory = $true)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Use the legacy OPC reader to reproduce Seq's package parsing, including
# [Content_Types].xml overrides such as the invalid //LICENSE in version 0.1.0.
Add-Type -AssemblyName WindowsBase
$resolvedPath = (Resolve-Path -LiteralPath $PackagePath).ProviderPath
$package = [System.IO.Packaging.Package]::Open($resolvedPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read)
try {
    $parts = @($package.GetParts())
    foreach ($required in @('/LICENSE', '/README.md', '/Seq.App.Nebula.nuspec')) {
        $partUri = [System.IO.Packaging.PackUriHelper]::CreatePartUri([Uri]::new($required, [UriKind]::Relative))
        if (-not $package.PartExists($partUri)) { throw "Required package part is missing: $required" }
        # Ensure the part's content can actually be opened, not just enumerated.
        $stream = $package.GetPart($partUri).GetStream()
        try { $stream.CopyTo([System.IO.Stream]::Null) } finally { $stream.Dispose() }
    }
    Write-Output "Seq OPC package reader verified $($parts.Count) parts in $resolvedPath"
}
finally { $package.Close() }
