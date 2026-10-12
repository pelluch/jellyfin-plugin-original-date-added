using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

[ApiController]
[Route("OriginalDateAdded/Arr")]
[Authorize(Policy = Policies.RequiresElevation)]
public class ArrController : ControllerBase
{
    private readonly ArrTester _tester;

    public ArrController(ArrTester tester)
    {
        _tester = tester;
    }

    [HttpPost("Test")]
    public async Task<ActionResult<ArrTestResult>> Test([FromBody] ArrConnection connection, CancellationToken cancellationToken)
        => await _tester.TestAsync(connection, cancellationToken).ConfigureAwait(false);
}
