<#
.SYNOPSIS
    Tests POST /api/audio with a real multipart/form-data request containing a minimal WAV file.
    No external tools needed — uses .NET HttpClient built into PowerShell.

.USAGE
    1. Start Azurite:   azurite --location C:/azurite --loose
    2. Start function:  func start    (in the project folder)
    3. Run this script: .\Requests\Test-Audio.ps1

    Optional overrides:
        .\Requests\Test-Audio.ps1 -CallType "S|TR" -CaseId "MY-CASE-001" -Phone "5551234567"
#>
param(
    [string]$BaseUrl   = "http://localhost:7071",
    [string]$CallType  = "D|FR",
    [string]$CaseId    = "TEST-$(Get-Date -Format 'yyyyMMddHHmmss')",
    [string]$Phone     = "9794920458",
    [string]$BrandId   = "4600",
    [string]$Timestamp = ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString())
)

# ── 1. Build a minimal valid WAV (44-byte header + 100 silent PCM samples) ──────
function New-MinimalWav {
    $sampleRate    = 8000
    $numChannels   = 1
    $bitsPerSample = 8
    $numSamples    = 100
    $dataSize      = $numSamples * $numChannels * ($bitsPerSample / 8)
    $fileSize      = 44 + $dataSize
    $wav           = New-Object byte[] $fileSize

    # RIFF chunk
    $enc = [System.Text.Encoding]::ASCII
    $enc.GetBytes("RIFF").CopyTo($wav, 0)
    [BitConverter]::GetBytes([int32](36 + $dataSize)).CopyTo($wav, 4)
    $enc.GetBytes("WAVE").CopyTo($wav, 8)

    # fmt  sub-chunk
    $enc.GetBytes("fmt ").CopyTo($wav, 12)
    [BitConverter]::GetBytes([int32]16).CopyTo($wav, 16)          # Subchunk1Size
    [BitConverter]::GetBytes([int16]1).CopyTo($wav, 20)           # PCM = 1
    [BitConverter]::GetBytes([int16]$numChannels).CopyTo($wav, 22)
    [BitConverter]::GetBytes([int32]$sampleRate).CopyTo($wav, 24)
    $byteRate = $sampleRate * $numChannels * ($bitsPerSample / 8)
    [BitConverter]::GetBytes([int32]$byteRate).CopyTo($wav, 28)
    $blockAlign = $numChannels * ($bitsPerSample / 8)
    [BitConverter]::GetBytes([int16]$blockAlign).CopyTo($wav, 32)
    [BitConverter]::GetBytes([int16]$bitsPerSample).CopyTo($wav, 34)

    # data sub-chunk
    $enc.GetBytes("data").CopyTo($wav, 36)
    [BitConverter]::GetBytes([int32]$dataSize).CopyTo($wav, 40)
    for ($i = 44; $i -lt $fileSize; $i++) { $wav[$i] = 0x80 }   # silence

    return $wav
}

# ── 2. Build and send multipart/form-data ────────────────────────────────────────
Add-Type -AssemblyName System.Net.Http

$metadata = [ordered]@{
    callType  = $CallType
    caseId    = $CaseId
    phone     = $Phone
    timestamp = $Timestamp
    brandId   = $BrandId
} | ConvertTo-Json -Compress

Write-Host ""
Write-Host "=== Sending POST $BaseUrl/api/audio ===" -ForegroundColor Cyan
Write-Host "  callType : $CallType"
Write-Host "  caseId   : $CaseId"
Write-Host "  phone    : $Phone"
Write-Host "  brandId  : $BrandId"
Write-Host ""

$client  = [System.Net.Http.HttpClient]::new()
$content = [System.Net.Http.MultipartFormDataContent]::new()

# metadata part
$metaPart = [System.Net.Http.StringContent]::new(
    $metadata,
    [System.Text.Encoding]::UTF8,
    "application/json"
)
$content.Add($metaPart, "metadata")

# audio part
$wavBytes  = New-MinimalWav
$audioPart = [System.Net.Http.ByteArrayContent]::new($wavBytes)
$audioPart.Headers.ContentType = `
    [System.Net.Http.Headers.MediaTypeHeaderValue]::new("audio/wav")
$content.Add($audioPart, "audio", "test_$(Get-Date -Format 'HHmmss').wav")

try {
    $response = $client.PostAsync("$BaseUrl/api/audio", $content).GetAwaiter().GetResult()
    $body     = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

    $color = if ($response.StatusCode -eq "Accepted") { "Green" } else { "Red" }
    Write-Host "Status : $([int]$response.StatusCode) $($response.StatusCode)" -ForegroundColor $color
    Write-Host "Body   : $body" -ForegroundColor $color
    Write-Host ""

    if ($response.StatusCode -eq "Accepted") {
        Write-Host "✓ Success — check Azurite Storage Explorer for:" -ForegroundColor Green
        Write-Host "  • Blob container  : media  (the WAV file)"
        Write-Host "  • Queue           : audio-processing-queue  (the JSON message)"
        Write-Host "  • Table           : AudioProcessingLog  (the audit row)"
    }
}
catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
    Write-Host "Is 'func start' running?" -ForegroundColor Yellow
}
finally {
    $client.Dispose()
}
