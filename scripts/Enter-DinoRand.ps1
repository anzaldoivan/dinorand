[CmdletBinding()]
param(
    [string] $EnvFile = (Join-Path $PSScriptRoot '..\.env.windows')
)

$resolvedEnvFile = (Resolve-Path -LiteralPath $EnvFile -ErrorAction Stop).Path
$loaded = [System.Collections.Generic.List[string]]::new()
$lineNumber = 0

foreach ($rawLine in Get-Content -LiteralPath $resolvedEnvFile) {
    $lineNumber++
    $line = $rawLine.Trim()
    if ($line.Length -eq 0 -or $line.StartsWith('#')) {
        continue
    }

    $match = [regex]::Match($line, '^(?<name>[A-Za-z_][A-Za-z0-9_]*)=(?<value>.*)$')
    if (-not $match.Success) {
        throw "Invalid environment entry in '$resolvedEnvFile' at line $lineNumber. Expected NAME=VALUE."
    }

    $name = $match.Groups['name'].Value
    $value = $match.Groups['value'].Value.Trim()
    if ($value.Length -ge 2) {
        $quoted = ($value.StartsWith('"') -and $value.EndsWith('"')) -or
            ($value.StartsWith("'") -and $value.EndsWith("'"))
        if ($quoted) {
            $value = $value.Substring(1, $value.Length - 2)
        }
    }

    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    $loaded.Add($name)
}

Write-Host "Loaded $($loaded.Count) DinoRand environment variables from $resolvedEnvFile."
