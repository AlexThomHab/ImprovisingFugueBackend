using Improvising_Fugue.Services;
using Microsoft.AspNetCore.Mvc;

namespace Improvising_Fugue.Controllers;

public class CurriculumController(CurriculumService curriculum) : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ChapterDto>> Get(CancellationToken ct) => curriculum.GetTreeAsync(UserId, ct);
}
