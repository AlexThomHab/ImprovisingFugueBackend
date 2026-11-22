using Improvising_Fugue.Services;
using Microsoft.AspNetCore.Mvc;

namespace Improvising_Fugue.Controllers;

public class DashboardController(DashboardService dashboard) : ApiControllerBase
{
    [HttpGet]
    public Task<DashboardDto> Get(CancellationToken ct) => dashboard.GetAsync(UserId, ct);
}
