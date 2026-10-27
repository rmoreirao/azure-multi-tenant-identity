[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'Common.ps1')
Import-LocalConfiguration
Use-ProjectNode

[Uri] $origin = $null
if (-not [Uri]::TryCreate($env:Application__PublicBaseUrl, [UriKind]::Absolute, [ref] $origin) -or
    -not $origin.IsLoopback -or
    $origin.Scheme -ne 'https' -or $origin.AbsolutePath -ne '/' -or
    $origin.Query -or $origin.Fragment -or $origin.UserInfo) {
    throw 'Local development requires Application__PublicBaseUrl to be an HTTPS loopback origin registered with External ID.'
}
if ($env:Azure__UseManagedIdentity -eq 'true') {
    throw 'Local development uses the explicitly configured developer Azure credential, not the hosted managed identity.'
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'
Push-Location $script:RepositoryRoot
try {
    Invoke-Checked dotnet dev-certs https --check --trust
    Invoke-Checked npm run build
    Write-Host "Starting the local application at $($origin.GetLeftPart([UriPartial]::Authority))."
    Invoke-Checked dotnet run --project 'src\Kanban.Web' --no-launch-profile --no-restore --urls ($origin.GetLeftPart([UriPartial]::Authority))
}
finally {
    Pop-Location
}
