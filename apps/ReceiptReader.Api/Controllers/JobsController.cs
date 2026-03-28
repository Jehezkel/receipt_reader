using Microsoft.AspNetCore.Mvc;
using ReceiptReader.Api.Contracts;
using ReceiptReader.Api.Repositories;

namespace ReceiptReader.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class JobsController : ControllerBase
{
    private readonly IReceiptRepository _repository;

    public JobsController(IReceiptRepository repository)
    {
        _repository = repository;
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<JobResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JobResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipts = await _repository.ListAsync(cancellationToken);
        var receipt = receipts.FirstOrDefault(item => item.Job.Id == id);
        if (receipt is null)
        {
            return NotFound();
        }

        return Ok(new JobResponse
        {
            Id = receipt.Job.Id,
            ReceiptId = receipt.Id,
            Stage = receipt.Job.Stage,
            StartedAt = receipt.Job.StartedAt,
            FinishedAt = receipt.Job.FinishedAt,
            ErrorCode = receipt.Job.ErrorCode,
            Provider = receipt.Job.Provider
        });
    }
}
