#Requires -Version 5.1
<#
.SYNOPSIS
  Dispatcher-domain probe for ACC-001 (plan §6): verify the static probe is served on the WPF
  dispatcher and the debug outer launch/restart probe only records reservation/claim/IsDebugging
  and Start on the WPF callback, with all object/handle resolution on the DbgManager dispatcher.

.DESCRIPTION
  The probe needs an in-process observation surface (dispatcher log + object-access spy exported
  by the extension under test). The production extension exports no such surface, so this script
  reports WPF=? DbgManager=? in the canonical single-line summary and exits non-zero until the
  injection surface exists. ACC-001 treats that as a failed precondition assertion.
#>
[CmdletBinding()]
param([string]$BaseUrl = 'http://localhost:3000/')

$ErrorActionPreference = 'Continue'
$code = & curl.exe -s -o NUL -w "%{http_code}" --max-time 5 ($BaseUrl.TrimEnd('/') + '/health') 2>$null
if ("$code".Trim() -ne '200') {
    Write-Output "WPF=unavailable DbgManager=unavailable (health=$code)"
    exit 2
}
# No in-process dispatcher log endpoint exists in the production build.
Write-Output "WPF=unknown DbgManager=unknown (no in-process dispatcher probe surface)"
exit 2
