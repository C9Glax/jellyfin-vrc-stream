using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.VrcShare.Configuration;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VrcShare.Api;

/// <summary>
/// Server-side endpoints backing the "VR Share Link" button injected into item
/// detail pages, plus the injector script itself.
/// </summary>
[ApiController]
[Route("VrcShare")]
public class VrcShareController : ControllerBase
{
    private const string AutoPairedKeyName = "VRC Share (auto)";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAuthenticationManager _authenticationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="VrcShareController"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory used to call the jellyfin-vrc-stream proxy.</param>
    /// <param name="authenticationManager">Used to mint a Jellyfin API key for the proxy during pairing.</param>
    public VrcShareController(IHttpClientFactory httpClientFactory, IAuthenticationManager authenticationManager)
    {
        _httpClientFactory = httpClientFactory;
        _authenticationManager = authenticationManager;
    }

    /// <summary>
    /// Serves the client-side injector script embedded in this assembly. Must
    /// stay unauthenticated - it needs to load on every page, including the
    /// login page - the button it adds only appears for administrators.
    /// </summary>
    /// <returns>The injector script.</returns>
    [HttpGet("inject.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetInjectScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        // Embedded resource logical names are rooted at the assembly's RootNamespace
        // ("Jellyfin.Plugin.VrcShare"), not this controller's own namespace.
        var resourceName = $"{typeof(Plugin).Namespace}.inject.js";
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound();
        }

        Response.Headers["Cache-Control"] = "no-cache";
        return new FileStreamResult(stream, "application/javascript");
    }

    /// <summary>
    /// Mints a time-limited share link for a single media item by calling the
    /// jellyfin-vrc-stream proxy's POST /share endpoint server-side, so the
    /// proxy's admin key never reaches the browser. Requires an elevated
    /// (administrator) Jellyfin session - the same one already authenticating
    /// this request, no extra login needed.
    /// </summary>
    /// <param name="itemId">Jellyfin media item ID to share.</param>
    /// <param name="mode">"vod" or "live" (defaults to "vod").</param>
    /// <param name="ttlSeconds">Optional link lifetime override in seconds.</param>
    /// <returns>The proxy's JSON response, containing the share URL and expiry.</returns>
    [HttpPost("CreateLink")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> CreateLink(
        [FromQuery] string itemId,
        [FromQuery] string mode = "vod",
        [FromQuery] int? ttlSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return BadRequest("itemId is required");
        }

        var configError = RequireConfig(out var config);
        if (configError != null)
        {
            return configError;
        }

        var payload = new
        {
            media_id = itemId,
            mode,
            ttl_seconds = ttlSeconds ?? config.DefaultTtlSeconds
        };

        return await ProxyForwardAsync(config, HttpMethod.Post, "share", JsonContent.Create(payload)).ConfigureAwait(false);
    }

    /// <summary>
    /// Mints a fresh Jellyfin API key and pushes it to the proxy's POST /pair
    /// endpoint, so the admin doesn't have to manually create a key in the
    /// Jellyfin dashboard and paste it into both the proxy's env var and this
    /// plugin's configuration. Only succeeds once - the proxy refuses to pair
    /// a second time until <see cref="Repair"/> resets it.
    /// </summary>
    /// <returns>Pairing result.</returns>
    [HttpPost("Pair")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> Pair()
    {
        var configError = RequireConfig(out var config, requireAdminKey: false, notConfiguredMessage: "Set Proxy Base URL before pairing.");
        if (configError != null)
        {
            return configError;
        }

        string accessToken;
        try
        {
            accessToken = await CreateAndFetchApiKeyAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Problem($"Failed to create a Jellyfin API key: {ex.Message}", statusCode: 500);
        }

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.ProxyBaseUrl.TrimEnd('/')}/pair")
        {
            Content = JsonContent.Create(new { api_key = accessToken })
        };

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return Problem($"Failed to reach the proxy at {config.ProxyBaseUrl}: {ex.Message}", statusCode: 502);
        }

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return Problem(
                "The proxy is already paired. Use Re-pair to reset it and pair again.",
                statusCode: 409);
        }

        if (!response.IsSuccessStatusCode)
        {
            return Problem($"Proxy returned {(int)response.StatusCode}: {body}", statusCode: 502);
        }

        config.AdminApiKey = accessToken;
        Plugin.Instance!.UpdateConfiguration(config);

        return Ok(new { paired = true });
    }

    /// <summary>
    /// Clears the proxy's current pairing (using the key this plugin already
    /// has) and pairs again with a newly minted key. Use this to recover when
    /// the proxy's pairing state was lost (e.g. restarted with non-persistent
    /// storage) or to deliberately rotate the key.
    /// </summary>
    /// <returns>Pairing result.</returns>
    [HttpPost("Repair")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> Repair()
    {
        var configError = RequireConfig(out var config, notConfiguredMessage: "Pair once before using Re-pair.");
        if (configError != null)
        {
            return configError;
        }

        var client = _httpClientFactory.CreateClient();
        using var unpairRequest = new HttpRequestMessage(HttpMethod.Delete, $"{config.ProxyBaseUrl.TrimEnd('/')}/pair");
        unpairRequest.Headers.Add("X-Admin-Key", config.AdminApiKey);

        HttpResponseMessage unpairResponse;
        try
        {
            unpairResponse = await client.SendAsync(unpairRequest).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return Problem($"Failed to reach the proxy at {config.ProxyBaseUrl}: {ex.Message}", statusCode: 502);
        }

        if (!unpairResponse.IsSuccessStatusCode)
        {
            var body = await unpairResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            return Problem(
                $"Proxy rejected the current key while unpairing ({(int)unpairResponse.StatusCode}: {body}). " +
                "It may already be unpaired - try Pair instead.",
                statusCode: 502);
        }

        return await Pair().ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the proxy's current cache/timeout settings (stream idle timeout,
    /// locked stream idle timeout, cleanup interval, max cache size) via the
    /// proxy's GET /settings endpoint.
    /// </summary>
    /// <returns>The proxy's JSON response.</returns>
    [HttpGet("Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> GetSettings()
    {
        var configError = RequireConfig(out var config);
        if (configError != null)
        {
            return configError;
        }

        return await ProxyForwardAsync(config, HttpMethod.Get, "settings").ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the proxy's cache/timeout settings via the proxy's PUT
    /// /settings endpoint. The request body is forwarded to the proxy as-is.
    /// </summary>
    /// <param name="body">The new settings, matching the proxy's RuntimeSettings shape.</param>
    /// <returns>The proxy's JSON response.</returns>
    [HttpPut("Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> UpdateSettings([FromBody] JsonElement body)
    {
        var configError = RequireConfig(out var config);
        if (configError != null)
        {
            return configError;
        }

        return await ProxyForwardAsync(config, HttpMethod.Put, "settings", JsonContent.Create(body)).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the proxy's quality profiles (built-in presets and custom) via
    /// the proxy's GET /profiles endpoint.
    /// </summary>
    /// <returns>The proxy's JSON response.</returns>
    [HttpGet("Profiles")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> GetProfiles()
    {
        var configError = RequireConfig(out var config);
        if (configError != null)
        {
            return configError;
        }

        return await ProxyForwardAsync(config, HttpMethod.Get, "profiles").ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new custom quality profile via the proxy's POST /profiles
    /// endpoint. The request body is forwarded to the proxy as-is.
    /// </summary>
    /// <param name="body">The new profile, matching the proxy's QualityProfile shape.</param>
    /// <returns>The proxy's JSON response.</returns>
    [HttpPost("Profiles")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> CreateProfile([FromBody] JsonElement body)
    {
        var configError = RequireConfig(out var config);
        if (configError != null)
        {
            return configError;
        }

        return await ProxyForwardAsync(config, HttpMethod.Post, "profiles", JsonContent.Create(body)).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates an existing custom quality profile via the proxy's PUT
    /// /profiles/{id} endpoint. The request body is forwarded to the proxy as-is.
    /// </summary>
    /// <param name="profileId">ID of the custom profile to update.</param>
    /// <param name="body">The updated profile, matching the proxy's QualityProfile shape.</param>
    /// <returns>The proxy's JSON response.</returns>
    [HttpPut("Profiles/{profileId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> UpdateProfile([FromRoute] string profileId, [FromBody] JsonElement body)
    {
        var configError = RequireConfig(out var config);
        if (configError != null)
        {
            return configError;
        }

        return await ProxyForwardAsync(config, HttpMethod.Put, $"profiles/{Uri.EscapeDataString(profileId)}", JsonContent.Create(body)).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a custom quality profile via the proxy's DELETE /profiles/{id}
    /// endpoint.
    /// </summary>
    /// <param name="profileId">ID of the custom profile to delete.</param>
    /// <returns>The proxy's JSON response.</returns>
    [HttpDelete("Profiles/{profileId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> DeleteProfile([FromRoute] string profileId)
    {
        var configError = RequireConfig(out var config);
        if (configError != null)
        {
            return configError;
        }

        return await ProxyForwardAsync(config, HttpMethod.Delete, $"profiles/{Uri.EscapeDataString(profileId)}").ConfigureAwait(false);
    }

    /// <summary>
    /// Validates that the plugin is configured enough to call the proxy, and
    /// hands back the configuration to use.
    /// </summary>
    /// <param name="config">The plugin's current configuration, if valid.</param>
    /// <param name="requireAdminKey">Whether <see cref="PluginConfiguration.AdminApiKey"/> must also be set.</param>
    /// <param name="notConfiguredMessage">Error message to return when validation fails.</param>
    /// <returns>An error <see cref="ActionResult"/> if validation failed, otherwise null.</returns>
    private ActionResult? RequireConfig(
        out PluginConfiguration config,
        bool requireAdminKey = true,
        string notConfiguredMessage = "VRC Share plugin is not configured. Set Proxy Base URL and Admin API Key on the plugin's settings page.")
    {
        config = Plugin.Instance?.Configuration!;
        if (Plugin.Instance?.Configuration == null
            || string.IsNullOrWhiteSpace(config.ProxyBaseUrl)
            || (requireAdminKey && string.IsNullOrWhiteSpace(config.AdminApiKey)))
        {
            return Problem(notConfiguredMessage, statusCode: 500);
        }

        return null;
    }

    /// <summary>
    /// Forwards a request to the jellyfin-vrc-stream proxy with the admin key
    /// attached, passing the proxy's raw JSON response straight through - no
    /// need to re-model it here, and it keeps the two sides from drifting out
    /// of sync on field names.
    /// </summary>
    /// <param name="config">Plugin configuration providing the proxy base URL and admin key.</param>
    /// <param name="method">HTTP method to use.</param>
    /// <param name="path">Path (without leading slash) relative to the proxy base URL.</param>
    /// <param name="content">Optional request body to forward.</param>
    /// <returns>The proxy's response, passed through, or a <see cref="Problem"/> result on failure.</returns>
    private async Task<ActionResult> ProxyForwardAsync(PluginConfiguration config, HttpMethod method, string path, HttpContent? content = null)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(method, $"{config.ProxyBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}");
        if (content != null)
        {
            request.Content = content;
        }

        request.Headers.Add("X-Admin-Key", config.AdminApiKey);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return Problem($"Failed to reach the proxy at {config.ProxyBaseUrl}: {ex.Message}", statusCode: 502);
        }

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Problem($"Proxy returned {(int)response.StatusCode}: {body}", statusCode: 502);
        }

        return Content(body, "application/json");
    }

    /// <summary>
    /// Creates a new Jellyfin API key via <see cref="IAuthenticationManager"/>
    /// and reads it back - CreateApiKey itself doesn't return the token.
    /// </summary>
    private async Task<string> CreateAndFetchApiKeyAsync()
    {
        await _authenticationManager.CreateApiKey(AutoPairedKeyName).ConfigureAwait(false);
        var keys = await _authenticationManager.GetApiKeys().ConfigureAwait(false);
        var match = keys
            .Where(k => k.AppName == AutoPairedKeyName)
            .OrderByDescending(k => k.DateCreated)
            .FirstOrDefault();

        if (match == null)
        {
            throw new InvalidOperationException("Key was created but could not be found afterward.");
        }

        return match.AccessToken;
    }
}
