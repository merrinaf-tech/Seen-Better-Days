param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [Parameter(Mandatory = $true)][string]$DesignRoot
)

$ErrorActionPreference = 'Stop'
$config = [xml](Get-Content -LiteralPath $ConfigPath -Raw)
$description = [string]$config.Publish.LongDescription
$match = [regex]::Match($description, '(?m)^## Hand-made designs - (\d+) so far\s*$')
if (-not $match.Success) {
    throw 'PublishConfiguration.xml must state the shipped design count in its Hand-made designs heading.'
}

$listed = [int]$match.Groups[1].Value
$files = @(Get-ChildItem -LiteralPath $DesignRoot -Recurse -File -Filter 'design.json')
if ($listed -ne $files.Count) {
    throw "The public listing says $listed shipped designs, but ShippedDesigns contains $($files.Count) design.json files."
}

Write-Output "Shipped design count verified: $listed."
