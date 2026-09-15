# Stdio MCP bridge for Codex. Reads the existing local bearer token without putting
# it in Codex config, command-line arguments, stdout, or environment variables.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseProxy = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(120)
$settingsPath = Join-Path $env:APPDATA 'ClaudeRevit/settings.json'
try {
    while ($null -ne ($line = [Console]::ReadLine())) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $rpc = $null
        try {
            $rpc = $line | ConvertFrom-Json
            $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            if (!$settings.McpEnabled -or !$settings.McpToken) { throw 'Enable MCP in ClaudeRevit settings first.' }
            $port = [int]$settings.McpPort
            if ($port -lt 1 -or $port -gt 65535) { throw 'Invalid Revit MCP port.' }
            $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "http://127.0.0.1:$port/mcp")
            try {
                $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $settings.McpToken)
                $request.Headers.TryAddWithoutValidation('Accept', 'application/json, text/event-stream') | Out-Null
                $request.Content = New-Object System.Net.Http.StringContent($line, [Text.Encoding]::UTF8, 'application/json')
                $response = $client.SendAsync($request).GetAwaiter().GetResult()
                try {
                    if (!$response.IsSuccessStatusCode) { throw "Revit MCP returned HTTP $([int]$response.StatusCode)." }
                    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    if ($null -ne $rpc.id -and $body) { [Console]::WriteLine($body) }
                } finally { $response.Dispose() }
            } finally { $request.Dispose() }
        } catch {
            # Never print request headers, settings, or raw exceptions containing tokens.
            if ($null -ne $rpc.id) {
                $errorResult = @{jsonrpc='2.0'; id=$rpc.id; error=@{code=-32603; message='Revit MCP unavailable. Start Revit with ClaudeRevit installed and MCP enabled; check the local server log.'}}
                [Console]::WriteLine(($errorResult | ConvertTo-Json -Depth 5 -Compress))
            }
        }
    }
} finally { $client.Dispose(); $handler.Dispose() }
