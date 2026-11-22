using Improvising_Fugue.Models;
using Improvising_Fugue.Services;
using Microsoft.AspNetCore.Mvc;

namespace Improvising_Fugue.Controllers;

public class ReviewsController(ReviewService reviews) : ApiControllerBase
{
    [HttpGet("due")]
    public Task<IReadOnlyList<ReviewCardDto>> Due(CancellationToken ct, [FromQuery] int limit = 20) => reviews.GetDueAsync(UserId, limit, ct);

    [HttpGet("practice")]
    public Task<IReadOnlyList<ReviewCardDto>> Practice(CancellationToken ct, [FromQuery] int count = 10) => reviews.GetPracticeAsync(UserId, count, ct);

    [HttpGet("summary")]
    public Task<ReviewSummaryDto> Summary(CancellationToken ct) => reviews.GetSummaryAsync(UserId, ct);

    [HttpPost("{stateId:guid}/rate")]
    public Task<RateResultDto> Rate(Guid stateId, RateReviewRequest req, CancellationToken ct) =>
        reviews.RateAsync(UserId, stateId, req.Rating, req.Practice, ct);
}
