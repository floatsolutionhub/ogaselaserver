using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Marketing;
using Ogasela.Shared;

namespace Ogasela.Application.Marketing.Testimonials;

/// <summary>Public - published testimonials for the ogasela.com homepage, newest first.</summary>
public sealed record GetPublishedTestimonialsQuery : IRequest<Result<IReadOnlyList<PublishedTestimonial>>>;

/// <summary>Control Portal - every testimonial, newest first.</summary>
public sealed record GetAllTestimonialsQuery : IRequest<Result<IReadOnlyList<AdminTestimonial>>>;

/// <summary>ConsentConfirmed must be true: staff vouch that the person agreed to be quoted.</summary>
public sealed record CreateTestimonialCommand(
    string Quote, string Name, string Role, int Rating, bool IsPublished, bool ConsentConfirmed)
    : IRequest<Result<AdminTestimonial>>;

public sealed record UpdateTestimonialCommand(Guid Id, string Quote, string Name, string Role, int Rating) : IRequest<Result>;

public sealed record SetTestimonialPublishedCommand(Guid Id, bool IsPublished) : IRequest<Result>;

public sealed record DeleteTestimonialCommand(Guid Id) : IRequest<Result>;

/// <summary>What the website shows - no staff-only fields.</summary>
public sealed record PublishedTestimonial(Guid Id, string Quote, string Name, string Role, int Rating);

public sealed record AdminTestimonial(
    Guid Id,
    string Quote,
    string Name,
    string Role,
    int Rating,
    bool IsPublished,
    DateTime ConsentConfirmedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public static class TestimonialErrors
{
    public static readonly Error NotFound = new("Testimonial.NotFound", "That testimonial no longer exists.");
}

public sealed class CreateTestimonialCommandValidator : AbstractValidator<CreateTestimonialCommand>
{
    public CreateTestimonialCommandValidator()
    {
        RuleFor(x => x.Quote).NotEmpty().MaximumLength(Testimonial.QuoteMaxLength);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Testimonial.NameMaxLength);
        RuleFor(x => x.Role).NotEmpty().MaximumLength(Testimonial.RoleMaxLength);
        RuleFor(x => x.Rating).InclusiveBetween(1, 5);
        RuleFor(x => x.ConsentConfirmed).Equal(true)
            .WithMessage("Only add testimonials from real users who agreed to be quoted.");
    }
}

public sealed class UpdateTestimonialCommandValidator : AbstractValidator<UpdateTestimonialCommand>
{
    public UpdateTestimonialCommandValidator()
    {
        RuleFor(x => x.Quote).NotEmpty().MaximumLength(Testimonial.QuoteMaxLength);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Testimonial.NameMaxLength);
        RuleFor(x => x.Role).NotEmpty().MaximumLength(Testimonial.RoleMaxLength);
        RuleFor(x => x.Rating).InclusiveBetween(1, 5);
    }
}

public sealed class TestimonialHandlers :
    IRequestHandler<GetPublishedTestimonialsQuery, Result<IReadOnlyList<PublishedTestimonial>>>,
    IRequestHandler<GetAllTestimonialsQuery, Result<IReadOnlyList<AdminTestimonial>>>,
    IRequestHandler<CreateTestimonialCommand, Result<AdminTestimonial>>,
    IRequestHandler<UpdateTestimonialCommand, Result>,
    IRequestHandler<SetTestimonialPublishedCommand, Result>,
    IRequestHandler<DeleteTestimonialCommand, Result>
{
    /// <summary>The homepage shows a 3-column grid; more than this is a wall of text.</summary>
    public const int MaxPublished = 6;

    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditLogger _auditLogger;
    private readonly IDateTime _dateTime;

    public TestimonialHandlers(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IAuditLogger auditLogger, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _auditLogger = auditLogger;
        _dateTime = dateTime;
    }

    public async Task<Result<IReadOnlyList<PublishedTestimonial>>> Handle(
        GetPublishedTestimonialsQuery request, CancellationToken cancellationToken)
    {
        var published = await _dbContext.Testimonials
            .AsNoTracking()
            .Where(t => t.IsPublished)
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Take(MaxPublished)
            .Select(t => new PublishedTestimonial(t.Id, t.Quote, t.Name, t.Role, t.Rating))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<PublishedTestimonial>>(published);
    }

    public async Task<Result<IReadOnlyList<AdminTestimonial>>> Handle(
        GetAllTestimonialsQuery request, CancellationToken cancellationToken)
    {
        var all = await _dbContext.Testimonials
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<AdminTestimonial>>(all.Select(ToAdmin).ToList());
    }

    public async Task<Result<AdminTestimonial>> Handle(CreateTestimonialCommand request, CancellationToken cancellationToken)
    {
        var actorId = _currentUser.UserId!.Value;
        var testimonial = Testimonial.Create(
            request.Quote.Trim(), request.Name.Trim(), request.Role.Trim(), request.Rating, request.IsPublished,
            actorId, _dateTime.UtcNow);
        _dbContext.Testimonials.Add(testimonial);

        await _auditLogger.LogAsync(
            actorId, "Testimonial.Created", nameof(Testimonial), testimonial.Id, null, Snapshot(testimonial), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success(ToAdmin(testimonial));
    }

    public async Task<Result> Handle(UpdateTestimonialCommand request, CancellationToken cancellationToken)
    {
        var testimonial = await _dbContext.Testimonials.FirstOrDefaultAsync(t => t.Id == request.Id, cancellationToken);
        if (testimonial is null)
        {
            return Result.Failure(TestimonialErrors.NotFound);
        }

        var before = Snapshot(testimonial);
        testimonial.Update(request.Quote.Trim(), request.Name.Trim(), request.Role.Trim(), request.Rating, _dateTime.UtcNow);

        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, "Testimonial.Updated", nameof(Testimonial), testimonial.Id, before,
            Snapshot(testimonial), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> Handle(SetTestimonialPublishedCommand request, CancellationToken cancellationToken)
    {
        var testimonial = await _dbContext.Testimonials.FirstOrDefaultAsync(t => t.Id == request.Id, cancellationToken);
        if (testimonial is null)
        {
            return Result.Failure(TestimonialErrors.NotFound);
        }

        var before = Snapshot(testimonial);
        testimonial.SetPublished(request.IsPublished, _dateTime.UtcNow);

        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, request.IsPublished ? "Testimonial.Published" : "Testimonial.Unpublished",
            nameof(Testimonial), testimonial.Id, before, Snapshot(testimonial), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> Handle(DeleteTestimonialCommand request, CancellationToken cancellationToken)
    {
        var testimonial = await _dbContext.Testimonials.FirstOrDefaultAsync(t => t.Id == request.Id, cancellationToken);
        if (testimonial is null)
        {
            return Result.Failure(TestimonialErrors.NotFound);
        }

        _dbContext.Testimonials.Remove(testimonial);
        // No quote or name in the audit entry - a deletion is often the person withdrawing consent.
        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, "Testimonial.Deleted", nameof(Testimonial), testimonial.Id,
            JsonSerializer.Serialize(new { testimonial.IsPublished, testimonial.CreatedAt }), null, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private static string Snapshot(Testimonial t) =>
        JsonSerializer.Serialize(new { t.Quote, t.Name, t.Role, t.Rating, t.IsPublished });

    private static AdminTestimonial ToAdmin(Testimonial t) => new(
        t.Id, t.Quote, t.Name, t.Role, t.Rating, t.IsPublished, t.ConsentConfirmedAt, t.CreatedAt, t.UpdatedAt);
}
