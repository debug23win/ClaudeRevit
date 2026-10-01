# Windows PowerShell stdio transport; the helper forwards session headers and cancellation
# concurrently with tool calls. Keep both files together (the release packages include them).
param([string]$SettingsPath = (Join-Path $env:APPDATA 'ClaudeRevit/settings.json'))
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'revit-mcp-bridge.cs') -ReferencedAssemblies System.Net.Http, System.Web.Extensions, System.Core
[ClaudeRevitMcpBridge]::Run($SettingsPath)
