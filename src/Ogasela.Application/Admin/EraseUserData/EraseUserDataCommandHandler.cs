using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.AdIntegrations;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Verification.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.Shared;

namespace Ogasela.Application.Admin.EraseUserData;

/// <summary>
/// Orchestrates an NDPA data-subject deletion request across every module holding this user's
/// identifying data: Phase 2 biometrics (erased per-verification via the existing
/// IBiometricDataErasureService), Phase 10 ad account connections (revoked, best-effort against
/// the live platform - a platform-side failure never blocks the erasure itself, since the
/// AdAccountConnection row and its tokens are erased either way), and the User/SellerProfile PII
/// fields themselves (anonymized, not deleted outright, so Transactions/AuditLogs/Reviews keep a
/// valid foreign key for financial and audit history as required).
/// Conversations and messages are deliberately left untouched: everyone the user did business
/// with keeps the chat for reference (see AdminUsersTests' *_KeepsTheirChats_* tests).
/// </summary>
public sealed class EraseUserDataCommandHandler : IRequestHandler<EraseUserDataCommand, Result<EraseUserDataReceipt>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IBiometricDataErasureService _biometricErasureService;
    private readonly IAdPlatformClientFactory _adPlatformClientFactory;
    private readonly ITokenEncryptor _tokenEncryptor;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<EraseUserDataCommandHandler> _logger;

    public EraseUserDataCommandHandler(
        IApplicationDbContext dbContext,
        ICurrentUserService currentUser,
        IDateTime dateTime,
        IPasswordHasher passwordHasher,
        IBiometricDataErasureService biometricErasureService,
        IAdPlatformClientFactory adPlatformClientFactory,
        ITokenEncryptor tokenEncryptor,
        IAuditLogger auditLogger,
        ILogger<EraseUserDataCommandHandler> logger)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
        _passwordHasher = passwordHasher;
        _biometricErasureService = biometricErasureService;
        _adPlatformClientFactory = adPlatformClientFactory;
        _tokenEncryptor = tokenEncryptor;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public async Task<Result<EraseUserDataReceipt>> Handle(EraseUserDataCommand request, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<EraseUserDataReceipt>(AdminErrors.UserNotFound);
        }

        var now = _dateTime.UtcNow;
        var sellerProfile = await _dbContext.SellerProfiles.FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);

        var verificationsErased = 0;
        var connectionsRevoked = 0;

        if (sellerProfile is not null)
        {
            var verifications = await _dbContext.BiometricVerifications
                .Where(v => v.SellerId == sellerProfile.Id && !v.IsErased)
                .Select(v => v.Id)
                .ToListAsync(cancellationToken);

            foreach (var verificationId in verifications)
            {
                await _biometricErasureService.EraseAsync(verificationId, cancellationToken);
                verificationsErased++;
            }

            var connections = await _dbContext.AdAccountConnections
                .Where(c => c.SellerId == sellerProfile.Id && c.RevokedAt == null)
                .ToListAsync(cancellationToken);

            foreach (var connection in connections)
            {
                await TryRevokePlatformSideAsync(connection, cancellationToken);
                connection.Revoke(now);
                connectionsRevoked++;
            }

            sellerProfile.Anonymize();
        }

        var refreshTokens = await _dbContext.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var refreshToken in refreshTokens)
        {
            refreshToken.Revoke(now);
        }

        // Identity documents are personal data too - remove the records (their files are private and
        // unreachable once the records are gone; storage cleanup can sweep them).
        var documents = await _dbContext.UserDocuments.Where(d => d.UserId == user.Id).ToListAsync(cancellationToken);
        _dbContext.UserDocuments.RemoveRange(documents);

        // Without this a Google/Apple/Facebook user could sign straight back into the anonymized account.
        var externalLogins = await _dbContext.ExternalLogins.Where(l => l.UserId == user.Id).ToListAsync(cancellationToken);
        _dbContext.ExternalLogins.RemoveRange(externalLogins);

        var changeRequests = await _dbContext.ProfileChangeRequests.Where(r => r.UserId == user.Id).ToListAsync(cancellationToken);
        _dbContext.ProfileChangeRequests.RemoveRange(changeRequests);

        // Hashing a random value (never a real password) so the account is permanently unable to
        // authenticate - Verify() can never match it, by construction.
        user.Anonymize(_passwordHasher.Hash(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")), now);

        var receipt = new EraseUserDataReceipt(
            user.Id, verificationsErased, connectionsRevoked, refreshTokens.Count, sellerProfile is not null, now);

        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value,
            "User.DataErasure",
            nameof(User),
            user.Id,
            beforeJson: null,
            afterJson: JsonSerializer.Serialize(receipt),
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(receipt);
    }

    private async Task TryRevokePlatformSideAsync(Domain.AdIntegrations.AdAccountConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var client = _adPlatformClientFactory.GetClient(connection.Platform);
            var accessToken = _tokenEncryptor.Decrypt(connection.EncryptedAccessToken);
            await client.RevokeConnectionAsync(accessToken, cancellationToken);
        }
        catch (Exception ex)
        {
            // The erasure must proceed regardless - the connection row (and its encrypted
            // tokens) is revoked/erased locally either way, so a platform-side outage can never
            // block an NDPA deletion deadline.
            _logger.LogWarning(ex, "Best-effort platform-side revoke failed during data erasure for connection {ConnectionId}", connection.Id);
        }
    }
}
