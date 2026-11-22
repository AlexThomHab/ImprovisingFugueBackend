using Improvising_Fugue.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Improvising_Fugue.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public abstract class ApiControllerBase : ControllerBase
{
    protected Guid UserId => User.GetUserId();
}
