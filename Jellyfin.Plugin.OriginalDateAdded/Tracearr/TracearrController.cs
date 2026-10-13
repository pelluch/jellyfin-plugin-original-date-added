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
    public async Task<ActionResult<TracearrTestResult>> Test([FromBody] TracearrConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var servers = await _client.GetJellyfinServersAsync(connection, cancellationToken).ConfigureAwait(false);
            if (servers.Count == 0)
            {
                return new TracearrTestResult(false, "Connected, but Tracearr has no Jellyfin servers. Add this Jellyfin to Tracearr first.", servers);
            }

            return new TracearrTestResult(true, string.Create(CultureInfo.InvariantCulture, $"Connected. Tracearr has {servers.Count} Jellyfin server(s)."), servers);
        }
        catch (TracearrException ex)
        {
            return new TracearrTestResult(false, ex.Message, null);
        }
    }
}
