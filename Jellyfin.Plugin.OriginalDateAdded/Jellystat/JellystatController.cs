using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OriginalDateAdded.Jellystat;

[ApiController]
[Route("OriginalDateAdded/Jellystat")]
[Authorize(Policy = Policies.RequiresElevation)]
public class JellystatController : ControllerBase
{
    private readonly JellystatClient _client;
    private readonly IApplicationHost _applicationHost;

    public JellystatController(JellystatClient client, IApplicationHost applicationHost)
    {
        _client = client;
        _applicationHost = applicationHost;
    }

    [HttpPost("Test")]
    public async Task<ActionResult<JellystatTestResult>> Test([FromBody] JellystatConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var libraries = await _client.GetLibrariesAsync(connection, cancellationToken).ConfigureAwait(false);
            var ours = libraries.Count(library => library.BelongsTo(_applicationHost.SystemId));
            if (ours > 0)
            {
                return new JellystatTestResult(true, string.Create(CultureInfo.InvariantCulture, $"Connected. Jellystat tracks this server ({ours} libraries)."));
            }

            return new JellystatTestResult(
                false,
                libraries.Any(library => !library.Archived)
                    ? "Connected, but Jellystat tracks a different Jellyfin server, so its dates won't be used"
                    : "Connected, but Jellystat has no libraries");
        }
        catch (JellystatException ex)
        {
            return new JellystatTestResult(false, ex.Message);
        }
    }
}
