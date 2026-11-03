using Microsoft.Extensions.Logging;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Resolves a Genesys agent to the Ticketing user it corresponds to — the
/// one place the "Genesys User ID → <c>AspNetUsers.GenesysUserId</c> →
/// Ticketing user → id / roles / departments" chain is walked.
///
/// <para>
/// <b>The immutable Genesys User ID is the only key.</b> Display name, agent
/// name and email are never consulted: names are not unique and change, and
/// an email match alone must never let a request act as a Ticketing user.
/// <c>GenesysEmail</c> on the user is informational, and that is all.
/// </para>
///
/// <para>
/// <b>Nothing is provisioned.</b> An unknown Genesys agent answers
/// <see cref="GenesysAgentResolutionOutcome.NotMapped"/> — a controlled,
/// distinct outcome, not "user not found" and not a silently created
/// account. Mapping is an administrative act performed on the Ticketing
/// user beforehand.
/// </para>
///
/// <para>
/// <b>Active under the existing rule.</b> A mapped user must still be a
/// valid, active employee — the same <c>ActiveEmployeeRequirement</c> every
/// authenticated request already passes — so a deactivated agent cannot be
/// recorded as handling anything (<see cref="GenesysAgentResolutionOutcome.Inactive"/>).
/// </para>
/// </summary>
public sealed class GenesysAgentResolutionAppService(
    IGenesysAgentMappingRepository mappingRepository,
    IUserRoleReader userRoleReader,
    IUserDepartmentAssignmentRepository departmentAssignmentRepository,
    IServiceIdentityRegistry? serviceIdentities = null,
    ILogger<GenesysAgentResolutionAppService>? logger = null,
    GenesysOptions? options = null,
    IUserAccountManager? accountManager = null)
{
    public const int GenesysUserIdMaxLength = 64;

    public async Task<GenesysAgentResolutionResult> ResolveByGenesysUserIdAsync(
        string? genesysUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(genesysUserId))
        {
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.Invalid, "genesysUserId is required.");
        }

        var value = genesysUserId.Trim();
        if (value.Length > GenesysUserIdMaxLength)
        {
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.Invalid, $"genesysUserId must be at most {GenesysUserIdMaxLength} characters.");
        }

        var mapped = await mappingRepository.FindByGenesysUserIdAsync(value, cancellationToken);
        if (mapped is null)
        {
            logger?.LogWarning("Genesys User ID {GenesysUserId} is not mapped to any Ticketing user (AspNetUsers.GenesysUserId).", value);
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.NotMapped, "Genesys agent is not mapped to a Ticketing user.");
        }

        // Mapping failures are logged by Genesys user id only (an opaque id, not a credential) so they can be found and fixed; never by name.
        if (mapped.IsAmbiguous)
        {
            logger?.LogError("Genesys agent mapping is AMBIGUOUS: more than one Ticketing user carries Genesys User ID {GenesysUserId}. The id is not resolved to anyone; fix AspNetUsers.GenesysUserId (the unique index UX_AspNetUsers_GenesysUserId must exist).", value);
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.Ambiguous, "More than one Ticketing user is mapped to this Genesys agent id.");
        }

        if (serviceIdentities?.IsServiceIdentity(mapped.UserId) == true)
        {
            logger?.LogWarning("Genesys User ID {GenesysUserId} is mapped to a service identity; it is never used as a handling agent.", value);
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.ServiceAccount, "The Ticketing user mapped to this Genesys id is a service account, not an agent.");
        }

        if (!mapped.IsActive)
        {
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.Inactive, "The Ticketing user mapped to this Genesys agent is deactivated.");
        }

        var roles = await userRoleReader.GetRolesAsync(mapped.UserId, cancellationToken);
        var departments = await departmentAssignmentRepository.GetByEmployeeIdAsync(mapped.UserId, cancellationToken);

        return new GenesysAgentResolutionResult(
            GenesysAgentResolutionOutcome.Success,
            mapped,
            roles,
            departments.Select(a => a.DepartmentId).Distinct().ToList());
    }

    /// <summary>
    /// Resolves the <c>senderId</c> of a <c>HumanAgent</c> transcript line to a Ticketing user.
    ///
    /// <para>
    /// <b>1. The Genesys User ID (GUID) is the contract and always goes first</b> - <see cref="ResolveByGenesysUserIdAsync"/>, unchanged. Any
    /// outcome other than <c>NotMapped</c> (mapped, inactive, ambiguous, service account, invalid) is final: an id that maps to somebody is
    /// never second-guessed by an e-mail lookup.
    /// </para>
    ///
    /// <para>
    /// <b>2. Fallback, only for an e-mail-form id that maps to nobody</b> (and only while <c>Genesys:AllowEmailSenderIdFallback</c> is on). The
    /// value must be a bare, well-formed e-mail address; it is matched as a WHOLE ADDRESS against <c>AspNetUsers.NormalizedEmail</c> /
    /// <c>GenesysEmail</c>. Display names, partial addresses and the sender's name are never consulted. The match is accepted only when it
    /// is exactly ONE distinct user who is an active employee, not a service identity, not locked out, and holds the CS Agent role.
    /// Zero matches = <c>NotMapped</c>; two or more = <c>Ambiguous</c> (nobody is picked); the rest are rejected with their own outcome.
    /// Deterministic: the same e-mail and data always give the same answer, independent of row order.
    /// </para>
    /// </summary>
    public async Task<GenesysAgentResolutionResult> ResolveSenderIdAsync(
        string? senderId, CancellationToken cancellationToken = default)
    {
        var byGuid = await ResolveByGenesysUserIdAsync(senderId, cancellationToken);
        if (byGuid.Outcome != GenesysAgentResolutionOutcome.NotMapped
            || options?.AllowEmailSenderIdFallback == false
            || !TryNormalizeEmail(senderId, out var email))
        {
            return byGuid;
        }

        var matches = await mappingRepository.FindByEmailAsync(email, cancellationToken);
        if (matches.Count == 0)
        {
            return byGuid;   // NotMapped - nobody carries this e-mail
        }

        if (matches.Select(m => m.UserId).Distinct().Count() > 1 || matches.Any(m => m.IsAmbiguous))
        {
            logger?.LogError("Genesys sender e-mail matches MORE THAN ONE Ticketing account; it is not resolved to anyone (fix the duplicate e-mail / GenesysEmail).");
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.Ambiguous, "More than one Ticketing user carries this sender e-mail.");
        }

        var user = matches[0];
        if (serviceIdentities?.IsServiceIdentity(user.UserId) == true)
        {
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.ServiceAccount, "The Ticketing user matching this sender e-mail is a service account, not an agent.");
        }

        if (!user.IsActive)
        {
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.Inactive, "The Ticketing user matching this sender e-mail is deactivated.");
        }

        if (accountManager is not null)
        {
            var account = await accountManager.GetAccountAsync(user.UserId, cancellationToken);
            if (account is null)
            {
                return GenesysAgentResolutionResult.Failure(
                    GenesysAgentResolutionOutcome.Inactive, "The Ticketing account matching this sender e-mail does not exist.");
            }

            if (account.IsLockedOut)
            {
                return GenesysAgentResolutionResult.Failure(
                    GenesysAgentResolutionOutcome.LockedOut, "The Ticketing account matching this sender e-mail is locked out.");
            }
        }

        var roles = await userRoleReader.GetRolesAsync(user.UserId, cancellationToken);
        if (!roles.Contains(Roles.CsAgent))
        {
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.NotCsAgent, "The Ticketing user matching this sender e-mail is not a CS Agent.");
        }

        var departments = await departmentAssignmentRepository.GetByEmployeeIdAsync(user.UserId, cancellationToken);

        // Record the user's stored Genesys id when there is one (the GUID, so the handler row looks like every other); otherwise the e-mail itself.
        var recorded = string.IsNullOrWhiteSpace(user.GenesysUserId) ? email : user.GenesysUserId;
        logger?.LogInformation("Genesys sender id resolved through the e-mail fallback to Ticketing user {UserId}; the contract id is the Genesys User ID (GUID).", user.UserId);

        return new GenesysAgentResolutionResult(
            GenesysAgentResolutionOutcome.Success,
            user with { GenesysUserId = recorded },
            roles,
            departments.Select(a => a.DepartmentId).Distinct().ToList());
    }

    /// <summary>True for a bare, well-formed e-mail address within the column limit (no display name, no list, no whitespace).</summary>
    public static bool TryNormalizeEmail(string? value, out string email)
    {
        email = string.Empty;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > GenesysUserIdMaxLength
            || trimmed.Any(char.IsWhiteSpace) || trimmed.Count(c => c == '@') != 1
            || !System.Net.Mail.MailAddress.TryCreate(trimmed, out var parsed)
            || !string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase)
            || !parsed.Host.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        email = trimmed;
        return true;
    }
}
