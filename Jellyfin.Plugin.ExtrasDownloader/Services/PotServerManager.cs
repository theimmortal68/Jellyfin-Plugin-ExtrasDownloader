using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtrasDownloader.Services;

/// <summary>
/// Manages the bgutil-ytdlp-pot-provider Node.js subprocess.
/// Provides POT (Proof of Origin Token) generation for YouTube bot detection bypass.
/// </summary>
public class PotServerManager : IDisposable
{
    private readonly ILogger<PotServerManager> _logger;
    private readonly HttpClient _httpClient;
    private Process? _serverProcess;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private bool _disposed;

    private const int DefaultPort = 4416;
    private const string PotServerDirectory = "pot-server";

    public PotServerManager(ILogger<PotServerManager> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Gets whether an external POT server URL is configured.
    /// </summary>
    public bool HasExternalServer => !string.IsNullOrEmpty(Plugin.Instance?.Configuration.PotProviderUrl);

    /// <summary>
    /// Gets the POT server URL (external or managed).
    /// </summary>
    public string ServerUrl => Plugin.Instance?.Configuration.PotProviderUrl ?? $"http://127.0.0.1:{DefaultPort}";

    /// <summary>
    /// Ensures the POT server is running. If an external URL is configured, validates it.
    /// Otherwise, starts the bundled server.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        // If external server configured, just check if it's available
        if (HasExternalServer)
        {
            return await CheckServerHealthAsync(cancellationToken);
        }

        // Try to start managed server
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            // Check if already running
            if (_serverProcess is { HasExited: false })
            {
                return await CheckServerHealthAsync(cancellationToken);
            }

            return await StartManagedServerAsync(cancellationToken);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// Checks if the POT server is healthy and responding.
    /// </summary>
    public async Task<bool> CheckServerHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<PingResponse>(
                $"{ServerUrl}/ping",
                cancellationToken);

            if (response != null)
            {
                _logger.LogDebug("POT server healthy: version={Version}, uptime={Uptime}s",
                    response.Version, response.ServerUptime);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "POT server health check failed at {Url}", ServerUrl);
        }

        return false;
    }

    /// <summary>
    /// Starts the managed POT server subprocess.
    /// </summary>
    private async Task<bool> StartManagedServerAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null) return false;

        // Find the bundled server
        var pluginDir = Path.GetDirectoryName(typeof(PotServerManager).Assembly.Location);
        var serverDir = Path.Combine(pluginDir!, PotServerDirectory);

        if (!Directory.Exists(serverDir))
        {
            _logger.LogWarning(
                "POT server directory not found at {Path}. " +
                "Either configure an external PotProviderUrl or ensure the server is bundled with the plugin.",
                serverDir);
            return false;
        }

        // Check for Node.js
        var nodePath = await FindNodeExecutableAsync();
        if (nodePath == null)
        {
            _logger.LogWarning(
                "Node.js not found. POT server requires Node.js to be installed. " +
                "Alternatively, configure an external PotProviderUrl.");
            return false;
        }

        var mainScript = Path.Combine(serverDir, "dist", "main.js");
        if (!File.Exists(mainScript))
        {
            _logger.LogWarning("POT server main script not found at {Path}", mainScript);
            return false;
        }

        try
        {
            _logger.LogInformation("Starting managed POT server on port {Port}", DefaultPort);

            var startInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = $"\"{mainScript}\" --port {DefaultPort}",
                WorkingDirectory = serverDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _serverProcess = new Process { StartInfo = startInfo };

            _serverProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogDebug("[POT] {Message}", e.Data);
            };

            _serverProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogWarning("[POT] {Message}", e.Data);
            };

            _serverProcess.Start();
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();

            // Wait for server to be ready
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000, cancellationToken);
                if (await CheckServerHealthAsync(cancellationToken))
                {
                    _logger.LogInformation("Managed POT server started successfully");
                    return true;
                }
            }

            _logger.LogError("POT server failed to start within timeout");
            StopManagedServer();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start managed POT server");
            return false;
        }
    }

    /// <summary>
    /// Stops the managed POT server if running.
    /// </summary>
    public void StopManagedServer()
    {
        if (_serverProcess == null || _serverProcess.HasExited) return;

        try
        {
            _logger.LogInformation("Stopping managed POT server");
            _serverProcess.Kill(entireProcessTree: true);
            _serverProcess.Dispose();
            _serverProcess = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping POT server");
        }
    }

    /// <summary>
    /// Finds the Node.js executable.
    /// </summary>
    private async Task<string?> FindNodeExecutableAsync()
    {
        var candidates = new[] { "node", "nodejs" };

        foreach (var candidate in candidates)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null) continue;

                var version = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    _logger.LogDebug("Found Node.js: {Candidate} {Version}", candidate, version.Trim());
                    return candidate;
                }
            }
            catch
            {
                // Not found, try next
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopManagedServer();
        _httpClient.Dispose();
        _startLock.Dispose();
    }

    private class PingResponse
    {
        [JsonPropertyName("server_uptime")]
        public double ServerUptime { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;
    }
}
