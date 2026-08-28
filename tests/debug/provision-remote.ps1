#Requires -RunAsAdministrator
<#
.SYNOPSIS
  One-time (reversible) provisioning for the dnSpy MCP remote-mode acceptance cases.

.DESCRIPTION
  ACC-023 / ACC-002 remote tuple: binding the MCP server to the VM's host-only IP
  (http://192.168.204.149:15100/) requires an http.sys URL reservation and an inbound
  firewall rule. Both need elevation once; the driver itself never elevates.

  Run elevated to provision:
      powershell -NoProfile -ExecutionPolicy Bypass -File provision-remote.ps1
  Run elevated to undo everything this script did:
      powershell -NoProfile -ExecutionPolicy Bypass -File provision-remote.ps1 -Undo

  The script is idempotent in both directions.
#>
[CmdletBinding()]
param(
    [switch]$Undo,
    [string]$BindIp = '192.168.204.149',
    [int]$Port = 15100,
    [string]$RuleName = 'dnspy-mcp-acc-remote'
)

$ErrorActionPreference = 'Stop'
$prefix = "http://${BindIp}:${Port}/"

if ($Undo) {
    netsh http delete urlacl url=$prefix
    netsh advfirewall firewall delete rule name="$RuleName"
    Write-Host "UNDONE: removed urlacl $prefix and firewall rule $RuleName"
    exit 0
}

netsh http add urlacl url=$prefix user=Everyone
netsh advfirewall firewall add rule name="$RuleName" dir=in action=allow protocol=TCP localport=$Port
Write-Host "PROVISIONED:"
netsh http show urlacl url=$prefix
Write-Host "Firewall rule '$RuleName' for TCP/$Port inbound: allow."
Write-Host "Undo any time with: powershell -File provision-remote.ps1 -Undo"
