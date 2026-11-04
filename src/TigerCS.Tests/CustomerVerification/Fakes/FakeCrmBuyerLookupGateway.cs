using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;

namespace TigerCS.Tests.CustomerVerification.Fakes;

/// <summary>In-memory <see cref="ICrmBuyerLookupGateway"/> double for <c>CrmBuyerLookupAppService</c> tests — returns whatever result was queued, regardless of the phone number searched.</summary>
public sealed class FakeCrmBuyerLookupGateway : ICrmBuyerLookupGateway
{
    private CrmBuyerLookupResult _result = CrmBuyerLookupResult.NotFound();

    public string? LastSearchedPhoneNumber { get; private set; }

    /// <summary>Number of times <see cref="GetBuyerByPhoneAsync"/> was actually invoked — proof that Customer History never calls CRM live (it must stay exactly what it was before a history call).</summary>
    public int CallCount { get; private set; }

    /// <summary>When set, the next calls throw it instead of answering - an unexpected fault in the CRM leg (not a classified gateway outcome).</summary>
    public Exception? ThrowOnCall { get; set; }

    public FakeCrmBuyerLookupGateway Returns(CrmBuyerLookupResult result)
    {
        _result = result;
        return this;
    }

    public Task<CrmBuyerLookupResult> GetBuyerByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        LastSearchedPhoneNumber = phoneNumber;
        CallCount++;
        if (ThrowOnCall is not null)
        {
            throw ThrowOnCall;
        }

        return Task.FromResult(_result);
    }
}
