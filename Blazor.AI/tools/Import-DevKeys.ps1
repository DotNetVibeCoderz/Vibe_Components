<#
.SYNOPSIS
  Copies your local API keys into the Gallery's .NET user-secrets (stored outside the repository).

.DESCRIPTION
  Reads key files from a folder on your machine and runs `dotnet user-secrets set` for the Gallery project.
  Nothing is written to the repository. Run it once per machine; re-run after rotating keys.

  Expected files (any can be missing):
    testkey.txt         sections "Azure OpenAI", "DeepSeek", "Anthropic" with "key: value" lines
    HFToken.txt         "Hugging-Face Access Token: hf_..."
    TypeSafeApiKey.txt  "typesafe api key: ..."

.EXAMPLE
  ./tools/Import-DevKeys.ps1 -KeyFolder C:\Users\me\Documents\CodeSandbox
#>
param(
    [Parameter(Mandatory)] [string] $KeyFolder,
    [string] $Project = "$PSScriptRoot/../samples/Blazor.AI.Gallery"
)

$ErrorActionPreference = 'Stop'

function Set-Secret([string] $name, [string] $value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return }
    dotnet user-secrets set $name $value --project $Project | Out-Null
    Write-Host "  set $name"
}

function Get-Value([string[]] $lines, [int] $start, [string] $prefix) {
    for ($i = $start; $i -lt $lines.Length; $i++) {
        if ($lines[$i].Trim().StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            return ($lines[$i] -split ':', 2)[1].Trim()
        }
    }
    return $null
}

function Find-Section([string[]] $lines, [string] $header) {
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i].Trim() -ieq $header) { return $i }
    }
    return -1
}

$keys = Join-Path $KeyFolder 'testkey.txt'
if (Test-Path $keys) {
    $lines = Get-Content $keys
    $index = 1

    $az = Find-Section $lines 'Azure OpenAI'
    if ($az -ge 0) {
        $p = "BlazorAI:Providers:$($index):"
        Set-Secret "${p}Name" 'azure'
        Set-Secret "${p}Kind" 'AzureOpenAI'
        Set-Secret "${p}Endpoint" (Get-Value $lines $az 'endpoint')
        Set-Secret "${p}ApiKey" (Get-Value $lines $az 'apikey')
        Set-Secret "${p}Model" 'gpt-5-mini'
        Set-Secret "${p}DisplayName" "Azure OpenAI $([char]0xB7) gpt-5-mini"
        $index++
    }

    $an = Find-Section $lines 'Anthropic'
    if ($an -ge 0) {
        $p = "BlazorAI:Providers:$($index):"
        Set-Secret "${p}Name" 'anthropic'
        Set-Secret "${p}Kind" 'Anthropic'
        Set-Secret "${p}ApiKey" (Get-Value $lines $an 'ApiKey')
        Set-Secret "${p}Model" 'claude-sonnet-5-5'
        Set-Secret "${p}Settings:workspaceId" (Get-Value $lines $an 'workspaceId')
        Set-Secret "${p}DisplayName" "Anthropic $([char]0xB7) Claude Sonnet 5.5"
        $index++
    }

    $ds = Find-Section $lines 'DeepSeek'
    if ($ds -ge 0) {
        $p = "BlazorAI:Providers:$($index):"
        Set-Secret "${p}Name" 'deepseek'
        Set-Secret "${p}Kind" 'DeepSeek'
        Set-Secret "${p}ApiKey" (Get-Value $lines $ds 'Deep Seek Api Key')
        Set-Secret "${p}Endpoint" (Get-Value $lines $ds 'Deep Seek Endpoint')
        Set-Secret "${p}Model" 'deepseek-v4-flash'
        Set-Secret "${p}DisplayName" "DeepSeek $([char]0xB7) v4 flash"
        $index++
    }
}

if ($index -gt 1) { Set-Secret "BlazorAI:DefaultProvider" "azure" }

$hf = Join-Path $KeyFolder 'HFToken.txt'
if (Test-Path $hf) { Set-Secret 'HuggingFace:Token' ((Get-Content $hf -Raw) -split ':', 2)[1].Trim() }

$ts = Join-Path $KeyFolder 'TypeSafeApiKey.txt'
if (Test-Path $ts) { Set-Secret 'TypeSafe:ApiKey' ((Get-Content $ts -Raw) -split ':', 2)[1].Trim() }

Write-Host "Done. Secrets live in your user profile, not in the repository."
