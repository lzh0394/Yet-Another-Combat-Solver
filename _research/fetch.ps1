# Helper for the broken web_search/web_fetch tools in this session.
# Usage examples (pwsh):
#   ./fetch.ps1 -Url 'https://raw.githubusercontent.com/google-deepmind/open_spiel/master/README.md'
#   ./fetch.ps1 -Wiki 'KataGo'
#   ./fetch.ps1 -Url 'https://example.com' -Html -Max 4000
# Notes: raw.githubusercontent.com, codeload.github.com, github.com HTML and
# en.wikipedia.org all work from this machine. api.github.com is rate-limited (403).

param(
    [string]$Url,
    [string]$Wiki,
    [int]$Max = 12000,
    [int]$Skip = 0,
    [switch]$Html
)

$ProgressPreference = 'SilentlyContinue'
$headers = @{ 'User-Agent' = 'Mozilla/5.0 (research)' }

if ($Wiki) {
    $enc = [uri]::EscapeDataString($Wiki)
    $Url = "https://en.wikipedia.org/w/api.php?action=query&prop=extracts&explaintext=1&format=json&redirects=1&titles=$enc"
    $json = Invoke-RestMethod -Uri $Url -Headers $headers -TimeoutSec 30
    $pages = $json.query.pages.PSObject.Properties.Value
    $text = ($pages | ForEach-Object { $_.extract }) -join "`n"
    if (-not $text) { Write-Output "(no wikipedia extract for '$Wiki')"; exit 1 }
} else {
    if (-not $Url) { Write-Output 'need -Url or -Wiki'; exit 1 }
    $resp = Invoke-WebRequest -Uri $Url -Headers $headers -TimeoutSec 40
    $text = $resp.Content
    if ($text -is [byte[]]) { $text = [System.Text.Encoding]::UTF8.GetString($text) }
}

if (-not $Html) {
    # crude HTML -> text for when the caller did not ask for raw HTML
    if ($text -match '<html|<!doctype|<div|<p>') {
        $text = $text -replace '(?s)<script.*?</script>', ' '
        $text = $text -replace '(?s)<style.*?</style>', ' '
        $text = $text -replace '(?s)<!--.*?-->', ' '
        $text = $text -replace '<br\s*/?>', "`n"
        $text = $text -replace '</(p|div|li|tr|h[1-6]|pre)>', "`n"
        $text = $text -replace '<[^>]+>', ' '
        $text = [System.Net.WebUtility]::HtmlDecode($text)
        $text = $text -replace '[ \t]+', ' '
        $text = $text -replace '(\r?\n\s*){3,}', "`n`n"
    }
}

if ($Skip -gt 0) { $text = $text.Substring([Math]::Min($Skip, $text.Length)) }
$out = $text.Substring(0, [Math]::Min($Max, $text.Length))
Write-Output $out
if ($text.Length -gt ($Skip + $Max)) { Write-Output "`n... [truncated: total $($text.Length) chars, use -Skip $($Skip + $Max)]" }
