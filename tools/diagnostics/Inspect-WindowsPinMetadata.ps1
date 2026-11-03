# Inspect-WindowsPinMetadata.ps1
# Read-only, local-only diagnostic. Do not run this through GitHub Actions.
# It reports aggregate status and counts, never paths, identifiers or clipboard text.
[CmdletBinding()]
param(
    [string] $PinnedFolder = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft\Windows\Clipboard\Pinned')
)

if ($env:GITHUB_ACTIONS -eq 'true') {
    throw 'This diagnostic must be run locally, not through GitHub Actions.'
}

$directories = 0
$unexpectedFiles = 0
$metadataPresent = 0
$metadataMissing = 0
$metadataUnreadable = 0
$metadataMalformed = 0
$metadataUnexpected = 0
$listedPins = 0
$seenIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$folderStatus = 'unavailable'

try {
    $folder = Get-Item -LiteralPath $PinnedFolder -Force -ErrorAction Stop
    if (-not $folder.PSIsContainer) {
        $folderStatus = 'not a directory'
    }
    else {
        $folderStatus = 'available'
        $entries = @(Get-ChildItem -LiteralPath $PinnedFolder -Force -ErrorAction Stop)
        foreach ($entry in $entries) {
            if (-not $entry.PSIsContainer) {
                $unexpectedFiles++
                continue
            }
            $directories++
            $metadata = Join-Path $entry.FullName 'metadata.json'
            try {
                $raw = [System.IO.File]::ReadAllText($metadata)
                $metadataPresent++
            }
            catch [System.IO.FileNotFoundException] { $metadataMissing++; continue }
            catch [System.IO.DirectoryNotFoundException] { $metadataMissing++; continue }
            catch { $metadataUnreadable++; continue }

            try {
                $document = [System.Text.Json.JsonDocument]::Parse($raw)
            }
            catch { $metadataMalformed++; continue }
            try {
                if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                    $metadataUnexpected++
                    continue
                }
                $items = @($document.RootElement.EnumerateObject() | Where-Object { $_.Name -ceq 'Items' -or $_.Name -ceq 'items' })
                if ($items.Count -ne 1 -or $items[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                    $metadataUnexpected++
                    continue
                }
                $valid = $true
                foreach ($item in $items[0].Value.EnumerateObject()) {
                    if ([string]::IsNullOrWhiteSpace($item.Name) -or
                        $item.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Object -or
                        -not $seenIds.Add($item.Name)) {
                        $valid = $false
                    }
                    $listedPins++
                }
                if (-not $valid) { $metadataUnexpected++ }
            }
            finally { $document.Dispose() }
        }
    }
}
catch [System.Management.Automation.ItemNotFoundException] { $folderStatus = 'missing' }
catch { $folderStatus = 'unavailable' }

Write-Host "Pinned metadata folder: $folderStatus"
Write-Host "Metadata containers: $directories"
Write-Host "Unexpected root files: $unexpectedFiles"
Write-Host "Metadata files read: $metadataPresent"
Write-Host "Metadata missing: $metadataMissing"
Write-Host "Metadata unreadable: $metadataUnreadable"
Write-Host "Metadata malformed: $metadataMalformed"
Write-Host "Metadata unexpected structure: $metadataUnexpected"
Write-Host "Listed pin count: $listedPins"
if ($folderStatus -eq 'available' -and $unexpectedFiles -eq 0 -and
    $metadataMissing -eq 0 -and $metadataUnreadable -eq 0 -and
    $metadataMalformed -eq 0 -and $metadataUnexpected -eq 0) {
    Write-Host 'Plugin snapshot assessment: verified'
}
else {
    Write-Host 'Plugin snapshot assessment: unverified'
}
