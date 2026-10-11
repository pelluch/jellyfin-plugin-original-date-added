using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OriginalDateAdded.Tracearr;

[ApiController]
[Route("OriginalDateAdded/Tracearr")]
[Authorize(Policy = Policies.RequiresElevation)]
public class TracearrController : ControllerBase
{
    private readonly TracearrClient _client;

    public TracearrController(TracearrClient client)
    {
        _client = client;
    }

    [HttpPost("Test")]
    public async Task<ActionResult<TracearrTestResult>> Test([FromBody] TracearrTestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var servers = await _client.GetJellyfinServersAsync(request.Url, request.ApiKey, cancellationToken).ConfigureAwait(false);
            var message = servers.Count == 0
                ? "Connected, but Tracearr has no Jellyfin servers"
                : string.Create(CultureInfo.InvariantCulture, $"Connected. Tracearr has {servers.Count} Jellyfin server(s).");
            return new TracearrTestResult(true, message, servers);
        }
        catch (TracearrException ex)
        {
            return new TracearrTestResult(false, ex.Message, []);
        }
    }
}
