using MediatR;
using Ogasela.Application.Settings;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.GetSafetyNotice;

/// <summary>The safety caption the app shows above the message box in every chat - edited by SuperAdmins in
/// Control Portal → Settings → Chat safety notice.</summary>
public sealed record GetSafetyNoticeQuery : IRequest<Result<SafetyNoticeResponse>>;

public sealed record SafetyNoticeResponse(string Text);

public sealed class GetSafetyNoticeQueryHandler : IRequestHandler<GetSafetyNoticeQuery, Result<SafetyNoticeResponse>>
{
    private readonly IPlatformSettings _settings;

    public GetSafetyNoticeQueryHandler(IPlatformSettings settings)
    {
        _settings = settings;
    }

    public async Task<Result<SafetyNoticeResponse>> Handle(GetSafetyNoticeQuery request, CancellationToken cancellationToken) =>
        Result.Success(new SafetyNoticeResponse(await _settings.MessagingSafetyNoticeAsync(cancellationToken)));
}
