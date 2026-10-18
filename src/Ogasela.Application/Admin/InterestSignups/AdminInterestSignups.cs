using System.Text;
using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Marketing;
using Ogasela.Shared;

namespace Ogasela.Application.Admin.InterestSignups;

/// <summary>Waiting-list and tester signups from ogasela.com, newest first. Null filters = all.</summary>
public sealed record GetAdminInterestSignupsQuery(
    InterestKind? Kind, InterestSignupStatus? Status, string? Search, int Page, int PageSize,
    InterestDevicePlatform? DevicePlatform = null)
    : IRequest<Result<AdminInterestSignupPage>>;

/// <summary>Counts for the page's header cards.</summary>
public sealed record GetInterestSignupSummaryQuery : IRequest<Result<InterestSignupSummary>>;

/// <summary>Same filters as the list, every matching row, as CSV - for sending launch or test-build invites.</summary>
public sealed record ExportInterestSignupsQuery(
    InterestKind? Kind, InterestSignupStatus? Status, string? Search, InterestDevicePlatform? DevicePlatform = null)
    : IRequest<Result<string>>;

/// <summary>Only the parts sent are changed.</summary>
public sealed record UpdateInterestSignupCommand(Guid Id, InterestSignupStatus? Status, string? Notes) : IRequest<Result>;

/// <summary>Hard delete - used when the person asks to be removed (NDPA right to erasure).</summary>
public sealed record DeleteInterestSignupCommand(Guid Id) : IRequest<Result>;

public sealed record AdminInterestSignupItem(
    Guid Id,
    InterestKind Kind,
    string Name,
    string Email,
    string? Phone,
    string? State,
    InterestIntent? Intent,
    InterestDevicePlatform? DevicePlatform,
    InterestSignupStatus Status,
    string? Notes,
    DateTime ConsentedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record AdminInterestSignupPage(IReadOnlyList<AdminInterestSignupItem> Items, int Page, int PageSize, int TotalCount);

public sealed record InterestSignupSummary(int WaitlistTotal, int WaitlistNew, int TestersTotal, int TestersNew, int Last7Days);

public static class InterestSignupErrors
{
    public static readonly Error NotFound = new("InterestSignup.NotFound", "That signup no longer exists.");
}

public sealed class UpdateInterestSignupCommandValidator : AbstractValidator<UpdateInterestSignupCommand>
{
    public UpdateInterestSignupCommandValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

public sealed class AdminInterestSignupHandlers :
    IRequestHandler<GetAdminInterestSignupsQuery, Result<AdminInterestSignupPage>>,
    IRequestHandler<GetInterestSignupSummaryQuery, Result<InterestSignupSummary>>,
    IRequestHandler<ExportInterestSignupsQuery, Result<string>>,
    IRequestHandler<UpdateInterestSignupCommand, Result>,
    IRequestHandler<DeleteInterestSignupCommand, Result>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditLogger _auditLogger;
    private readonly IDateTime _dateTime;

    public AdminInterestSignupHandlers(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IAuditLogger auditLogger, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _auditLogger = auditLogger;
        _dateTime = dateTime;
    }

    public async Task<Result<AdminInterestSignupPage>> Handle(GetAdminInterestSignupsQuery request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var signups = Filter(request.Kind, request.Status, request.Search, request.DevicePlatform);
        var total = await signups.CountAsync(cancellationToken);
        var items = await signups
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => ToItem(s))
            .ToListAsync(cancellationToken);

        return Result.Success(new AdminInterestSignupPage(items, page, pageSize, total));
    }

    public async Task<Result<InterestSignupSummary>> Handle(GetInterestSignupSummaryQuery request, CancellationToken cancellationToken)
    {
        var counts = await _dbContext.InterestSignups
            .GroupBy(s => new { s.Kind, IsNew = s.Status == InterestSignupStatus.New })
            .Select(g => new { g.Key.Kind, g.Key.IsNew, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var weekAgo = _dateTime.UtcNow.AddDays(-7);
        var last7Days = await _dbContext.InterestSignups.CountAsync(s => s.CreatedAt >= weekAgo, cancellationToken);

        int Sum(InterestKind kind, bool onlyNew) =>
            counts.Where(c => c.Kind == kind && (!onlyNew || c.IsNew)).Sum(c => c.Count);

        return Result.Success(new InterestSignupSummary(
            Sum(InterestKind.Waitlist, false), Sum(InterestKind.Waitlist, true),
            Sum(InterestKind.Tester, false), Sum(InterestKind.Tester, true),
            last7Days));
    }

    public async Task<Result<string>> Handle(ExportInterestSignupsQuery request, CancellationToken cancellationToken)
    {
        var rows = await Filter(request.Kind, request.Status, request.Search, request.DevicePlatform)
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .ToListAsync(cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("Kind,Name,Email,Phone,State,Intent,Device,Status,Notes,Signed up (UTC),Consented (UTC)");
        foreach (var s in rows)
        {
            csv.AppendLine(string.Join(',',
                Cell(s.Kind.ToString()), Cell(s.Name), Cell(s.Email), Cell(s.Phone), Cell(s.State),
                Cell(s.Intent?.ToString()), Cell(s.DevicePlatform?.ToString()), Cell(s.Status.ToString()), Cell(s.Notes),
                Cell(s.CreatedAt.ToString("yyyy-MM-dd HH:mm")), Cell(s.ConsentedAt.ToString("yyyy-MM-dd HH:mm"))));
        }

        // An export copies personal data out of the system, so who did it (and how much) is audited.
        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, "InterestSignup.Exported", nameof(InterestSignup), Guid.Empty, null,
            JsonSerializer.Serialize(new { request.Kind, request.Status, request.Search, request.DevicePlatform, Rows = rows.Count }),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(csv.ToString());
    }

    public async Task<Result> Handle(UpdateInterestSignupCommand request, CancellationToken cancellationToken)
    {
        var signup = await _dbContext.InterestSignups.FirstOrDefaultAsync(s => s.Id == request.Id, cancellationToken);
        if (signup is null)
        {
            return Result.Failure(InterestSignupErrors.NotFound);
        }

        var before = JsonSerializer.Serialize(new { signup.Status, signup.Notes });
        var now = _dateTime.UtcNow;
        if (request.Status is { } status)
        {
            signup.UpdateStatus(status, now);
        }

        if (request.Notes is not null)
        {
            signup.UpdateNotes(request.Notes, now);
        }

        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, "InterestSignup.Updated", nameof(InterestSignup), signup.Id, before,
            JsonSerializer.Serialize(new { signup.Status, signup.Notes }), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> Handle(DeleteInterestSignupCommand request, CancellationToken cancellationToken)
    {
        var signup = await _dbContext.InterestSignups.FirstOrDefaultAsync(s => s.Id == request.Id, cancellationToken);
        if (signup is null)
        {
            return Result.Failure(InterestSignupErrors.NotFound);
        }

        _dbContext.InterestSignups.Remove(signup);
        // Deliberately no name/email in the audit entry - keeping them would undo the erasure.
        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, "InterestSignup.Deleted", nameof(InterestSignup), signup.Id,
            JsonSerializer.Serialize(new { signup.Kind, signup.Status, signup.CreatedAt }), null, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private IQueryable<InterestSignup> Filter(
        InterestKind? kind, InterestSignupStatus? status, string? search, InterestDevicePlatform? devicePlatform)
    {
        var signups = _dbContext.InterestSignups.AsNoTracking();
        if (devicePlatform is { } device)
        {
            signups = signups.Where(s => s.DevicePlatform == device);
        }

        if (kind is { } k)
        {
            signups = signups.Where(s => s.Kind == k);
        }

        if (status is { } st)
        {
            signups = signups.Where(s => s.Status == st);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            signups = signups.Where(s =>
                s.Name.ToLower().Contains(term) || s.Email.Contains(term) || (s.Phone != null && s.Phone.Contains(term)));
        }

        return signups;
    }

    private static AdminInterestSignupItem ToItem(InterestSignup s) => new(
        s.Id, s.Kind, s.Name, s.Email, s.Phone, s.State, s.Intent, s.DevicePlatform, s.Status, s.Notes,
        s.ConsentedAt, s.CreatedAt, s.UpdatedAt);

    /// <summary>Quotes every cell and defuses spreadsheet formulas (a name like "=HYPERLINK(...)").</summary>
    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
