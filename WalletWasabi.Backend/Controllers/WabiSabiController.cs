using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WalletWasabi.Backend.Filters;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Models;

namespace WalletWasabi.Backend.Controllers
{
	[Produces("application/json")]
	[ApiController]
	[Route("[controller]")]
	public class WabiSabiController : ControllerBase
	{
		public WabiSabiController(IArenaRequestHandler handler)
		{
			RequestHandler = handler;
		}

		private IArenaRequestHandler RequestHandler { get; }

		[HttpPost("connection-confirmation")]
		[Idempotent]
		public Task<ConnectionConfirmationResponse> ConfirmConnectionAsync(ConnectionConfirmationRequest request, CancellationToken cancellableToken)
		{
			return RequestHandler.ConfirmConnectionAsync(request, cancellableToken);
		}

		[HttpPost("input-registration")]
		[Idempotent]
		public Task<InputRegistrationResponse> RegisterInputAsync(InputRegistrationRequest request, CancellationToken cancellableToken)
		{
			return RequestHandler.RegisterInputAsync(request, cancellableToken);
		}

		[HttpPost("output-registration")]
		[Idempotent]
		public Task<OutputRegistrationResponse> RegisterOutputAsync(OutputRegistrationRequest request, CancellationToken cancellableToken)
		{
			return RequestHandler.RegisterOutputAsync(request, cancellableToken);
		}

		[HttpPost("credential-issuance")]
		[Idempotent]
		public Task<ReissueCredentialResponse> ReissueCredentialAsync(ReissueCredentialRequest request, CancellationToken cancellableToken)
		{
			return RequestHandler.ReissueCredentialAsync(request, cancellableToken);
		}

		[HttpPost("input-unregistration")]
		public Task RemoveInputAsync(InputsRemovalRequest request, CancellationToken cancellableToken)
		{
			return RequestHandler.RemoveInputAsync(request, cancellableToken);
		}

		[HttpPost("transaction-signature")]
		public Task SignTransactionAsync(TransactionSignaturesRequest request, CancellationToken cancellableToken)
		{
			return RequestHandler.SignTransactionAsync(request, cancellableToken);
		}
	}
}
