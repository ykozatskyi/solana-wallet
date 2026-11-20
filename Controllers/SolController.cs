using SolanaWalletService.Models;
using SolanaWalletService.Payouts;
using SolanaWalletService.Solana;
using Microsoft.AspNetCore.Mvc;

namespace SolanaWalletService.Controllers;

[ApiController]
[Route("api/sol")]
internal sealed class SolController : ControllerBase
{
    private readonly Services.SolanaWalletService _wallet;
    private readonly PayoutService _payouts;
    private readonly ILogger<SolController> _logger;

    public SolController(Services.SolanaWalletService wallet, PayoutService payouts, ILogger<SolController> logger)
    {
        _wallet = wallet;
        _payouts = payouts;
        _logger = logger;
    }

    [HttpGet("address")]
    public async Task<ActionResult<WalletAddressDto>> GetAddress(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Ok(await _wallet.GetAddressAsync().ConfigureAwait(false));
    }

    [HttpGet("balance")]
    public Task<IActionResult> GetBalance(CancellationToken cancellationToken)
    {
        return Execute(async () => Ok(await _wallet.GetBalanceAsync(cancellationToken).ConfigureAwait(false)));
    }

    [HttpGet("transactions")]
    public Task<IActionResult> GetTransactions(
        [FromQuery] int limit = 10,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return Execute(async () =>
        {
            if (limit is < 1 or > Services.SolanaWalletService.MaxTransactionLimit || offset < 0)
                return BadRequest(new ErrorResponse("limit must be between 1 and 50, and offset must be zero or greater."));

            var page = await _wallet.GetTransactionsAsync(limit, offset, cancellationToken).ConfigureAwait(false);
            return Ok(page);
        });
    }

    [HttpPost("payouts")]
    [RequestSizeLimit(4_096)]
    public async Task<IActionResult> CreatePayout([FromBody] CreatePayoutRequest? request, CancellationToken cancellationToken)
    {
        if (!PayoutRequests.TryRead(request, out var command, out var error))
            return BadRequest(new ErrorResponse(error));

        var result = await _payouts.CreateAsync(command, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            PayoutResult.Ok ok => Accepted($"/api/sol/payouts/{ok.Payout.Id}", new PayoutAcceptedDto(ok.Payout.Id, ok.Payout.Status.ToString())),
            PayoutResult.Error failure => StatusCode(failure.Status, new ErrorResponse(failure.Message)),
            _ => StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse("Internal server error."))
        };
    }

    [HttpGet("payouts/{id}")]
    public async Task<IActionResult> GetPayout(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var payoutId))
            return BadRequest(new ErrorResponse("Payout id must be a UUID."));

        var payout = await _payouts.FindAsync(payoutId, cancellationToken).ConfigureAwait(false);
        return payout is null ? NotFound(new ErrorResponse("Payout was not found.")) : Ok(payout);
    }

    [HttpGet("payouts")]
    public async Task<IActionResult> ListPayouts(
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > PayoutLimits.MaxListLimit || offset < 0)
            return BadRequest(new ErrorResponse("limit must be between 1 and 50, and offset must be zero or greater."));

        return Ok(await _payouts.ListAsync(limit, offset, cancellationToken).ConfigureAwait(false));
    }

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest(new ErrorResponse("limit must be between 1 and 50, and offset must be zero or greater."));
        }
        catch (SolanaRpcException ex)
        {
            _logger.LogWarning("Wallet request failed ({Kind}).", ex.Kind);
            return StatusCode(MapStatus(ex.Kind), new ErrorResponse(ex.Message));
        }
    }

    private static int MapStatus(SolanaRpcErrorKind kind)
    {
        return kind switch
        {
            SolanaRpcErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
            SolanaRpcErrorKind.Timeout => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status502BadGateway
        };
    }
}
