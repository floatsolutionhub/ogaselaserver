# Ogasela API

ASP.NET Core 10 backend for Ogasela, built as a Clean Architecture solution.

- **Phase 0** — scaffolding: solution structure, EF Core + Postgres, Redis, MediatR, OpenAPI/Scalar, Serilog, Docker Compose, test projects.
- **Phase 1** — Identity, Auth & Accounts (see below): registration, OTP, login, refresh-token rotation, JWT auth, role policies.
- **Notifications** (see below) — real OTP SMS delivery via Termii and branded transactional email via Brevo.

## Solution structure

```
Ogasela.slnx
Directory.Build.props        # shared TFM (net10.0), Nullable, ImplicitUsings for every project
docker-compose.yml           # api + postgres:16 + redis:7
src/
  Ogasela.Domain/            # entities, enums, domain events, business-rule exceptions
  Ogasela.Application/       # MediatR commands/queries, DTOs, service interfaces
  Ogasela.Infrastructure/    # EF Core DbContext + migrations, repositories, external clients
  Ogasela.Api/                # Program.cs, DI composition root, OpenAPI/Scalar, endpoints
  Ogasela.Shared/             # Result<T>, cross-cutting types with no dependency on the other layers
tests/
  Ogasela.UnitTests/
  Ogasela.IntegrationTests/  # WebApplicationFactory + Testcontainers.PostgreSql
```

## Project reference rules

These are enforced by the `.csproj` files and must not be violated:

- `Ogasela.Domain` — no project references (pure domain layer).
- `Ogasela.Application` — references `Ogasela.Domain` and `Ogasela.Shared` only.
- `Ogasela.Infrastructure` — references `Ogasela.Domain` and `Ogasela.Application`.
- `Ogasela.Api` — references `Ogasela.Application` and `Ogasela.Infrastructure`.
- `Ogasela.Shared` — no project references; every layer may depend on it.

Dependencies always point inward, toward `Ogasela.Domain`. Never add a reference that would
let `Domain` or `Application` depend on `Infrastructure` or `Api`.

## Module-per-folder convention

`Ogasela.Domain`, `Ogasela.Application`, and `Ogasela.Infrastructure` are each organized into
the same set of module folders, one per business capability, instead of by technical type
(no solution-wide `Controllers/`, `Entities/`, `Services/` folders):

- `Accounts`
- `Verification`
- `Listings`
- `Promotions`
- `Payments`
- `Messaging`
- `Reviews`
- `Moderation`

When adding a feature, put it in the module folder it belongs to in each layer that needs it,
e.g. a new command lives at `Ogasela.Application/Listings/CreateListing/`, its entity at
`Ogasela.Domain/Listings/Listing.cs`, and its EF configuration or repository at
`Ogasela.Infrastructure/Listings/`. Within a module, prefer one sub-folder per
MediatR command/query (command or query + handler + validator + response DTO together)
over splitting by type.

## Upcoming abstraction interfaces

The following interfaces are **not implemented yet** — they are documented here so future
work lands in the right layer and module from the start. Each is a `Ogasela.Application`
interface (module folder noted below) implemented by a corresponding adapter in
`Ogasela.Infrastructure`:

- **`IFaceVerificationProvider`** (`Verification` module) — wraps the external identity/face
  verification provider used to verify user identity during onboarding. `SellerProfile` already
  has a `VerificationStatus` field (defaulting to `NotStarted`) for this to drive once it lands;
  nothing currently transitions it out of `NotStarted`.
- **`IPaymentGateway`** (`Payments` module) — wraps payment provider integrations (e.g.
  Paystack, Flutterwave) for charges, payouts, and webhooks.
- **`IAdPlatformClient`** (`Promotions` module) — wraps ad-platform integrations (e.g. Meta
  Marketing API, TikTok Business API) for boosting/promoting listings.

Define each interface in `Ogasela.Application/<Module>/` next to the use case that needs it,
and keep all provider-specific code (SDKs, HTTP clients, API keys) inside the matching
`Ogasela.Infrastructure/<Module>/` implementation — `Application` must never reference a
third-party SDK directly.

## Identity, Auth & Accounts (Phase 1)

Entities in `Ogasela.Domain/Accounts`: `User`, `SellerProfile` (created alongside `User` on
seller registration), `RefreshToken`. All three are anemic-by-design (private setters, a
static `Create` factory, and small behavior methods) — no EF or MediatR types leak into
`Domain`.

**Application ports.** Handlers never touch EF Core or Redis directly; they depend on
interfaces the Infrastructure layer implements:
- `IApplicationDbContext` (`Application/Common/Interfaces`) — `DbSet<T>` + `SaveChangesAsync`,
  implemented by `OgaselaDbContext`. Requires `Application` to reference the
  `Microsoft.EntityFrameworkCore` package (abstractions only, no provider) — this is the one
  deliberate exception to "Application has no infrastructure dependencies."
- `ICurrentUserService` (`Application/Common/Interfaces`) — implemented in `Ogasela.Api`
  (`Api/Common/CurrentUserService.cs`) since it wraps `IHttpContextAccessor`, which only the
  Api layer has a reason to depend on.
- `IDateTime` (`Application/Common/Interfaces`) — testable clock; implemented by
  `Infrastructure/Common/SystemDateTime`. Anything that compares against "now" (token expiry,
  rate-limit windows) goes through this rather than `DateTime.UtcNow` directly, so handler
  tests can control time.
- `IPasswordHasher`, `ITokenService`, `IOtpService` (`Application/Accounts/Interfaces`) —
  implemented by `PasswordHasher` (wraps ASP.NET Core Identity's `PasswordHasher<T>`),
  `JwtTokenService`, and the concrete `OtpService` respectively, all in
  `Ogasela.Infrastructure/Accounts`.

**OTP design.** `IOtpService`'s rate-limiting/verification *rules* (5 verify attempts per 15
minutes, 5-minute code TTL) live in `Application/Accounts/OtpService.cs` as plain C# against a
storage seam, `IOtpStore`. Only `IOtpStore` is Redis-specific
(`Infrastructure/Accounts/RedisOtpStore.cs`, using `IConnectionMultiplexer` directly for
atomic `INCR`/`EXPIRE` rather than `IDistributedCache`, since rate limiting needs atomicity).
Keeping the rule in `Application` behind `IOtpStore` is what makes it unit-testable with an
in-memory fake instead of a real Redis instance — see
`Ogasela.UnitTests/Accounts/OtpServiceTests.cs`. `RequestOtpCommandHandler` sends the generated
code via `ISmsSender` (see Notifications below); `IOtpService.PeekAsync` still exists for resend
flows and is how integration tests read the code without an SMS inbox.

**Master OTP.** `OtpSettings.MasterCode` (`Default_Global_OTP` in `.env`, config key
`Otp:MasterCode`) is accepted by `OtpService.VerifyAsync` in place of the generated code for every
OTP check, whether or not a code was sent; the generated code keeps working too. It still counts
against the attempt limit, and each use is logged as a warning. It is **honoured in every
environment, Production included** (by explicit request) - leave `Default_Global_OTP` empty to
turn it off. `Program.cs` logs a startup warning whenever it's active - see
`MasterOtpEnvironmentTests`.

**Refresh token rotation.** `AuthTokenIssuer` (`Application/Accounts`) is the single place that
mints an access/refresh pair and persists the (hashed) refresh token; both `LoginCommandHandler`
and `RefreshTokenCommandHandler` go through it so the two flows can't drift. On refresh,
`RefreshTokenCommandHandler` distinguishes a token that's revoked-via-logout
(`RevokedAt != null`, `ReplacedByTokenId == null`) from one that's revoked-via-rotation
(`ReplacedByTokenId != null`) — presenting the latter again means the token was copied/stolen,
so the handler revokes every other active refresh token for that user ("the token family" is
defined as all of a user's active `RefreshToken` rows, since the entity has no separate family
id). See `Ogasela.UnitTests/Accounts/RefreshTokenCommandHandlerTests.cs`.

**Auth wiring.** JWT bearer auth is configured in `Program.cs` (`Jwt:Secret`/`Issuer`/`Audience`
in configuration). One authorization policy per `UserRole` value is registered up front
(`options.AddPolicy(role, p => p.RequireRole(role))` for all six roles), even though only
Buyer/Seller-reachable endpoints exist so far — later phases add `[Authorize(Policy =
"Moderator")]` etc. directly without touching `Program.cs`. Controllers live in
`Ogasela.Api/Controllers/Accounts` (`AuthController`, `MeController`); `Result`/`Result<T>` is
translated to an `IActionResult` via `Api/Common/ResultExtensions.cs`, which maps specific
`Error.Code`s to HTTP status codes (e.g. `User.InvalidCredentials` → 401,
`Otp.RateLimited` → 429).

**Testing note.** Unit tests for handlers that need `IApplicationDbContext` use
`Ogasela.UnitTests/TestSupport/TestApplicationDbContext` (EF Core InMemory provider) rather
than referencing `Ogasela.Infrastructure` from the test project, to keep `Ogasela.UnitTests`
scoped to `Domain` + `Application` per the reference rules above.

## Notifications

A cross-cutting module (not one of the eight business modules above — SMS/email delivery is
used by every module, not owned by one) providing outbound SMS and branded email. Lives at
`Application/Notifications` (ports + the template) and `Infrastructure/Notifications`
(provider adapters):

- **`ISmsSender`** — implemented by `TermiiSmsSender`, which POSTs to Termii's
  `api/sms/send` (base URL, sender ID `FloathHub` in config; the API key is a secret, see
  below). `Application/Accounts/NigerianPhoneNumber.ToInternationalFormat` converts the app's
  local `0…` numbers to the `234…` format Termii expects.
- **`IEmailSender`** — implemented by `SmtpEmailSender` (MailKit), sending through Brevo's
  SMTP relay (`smtp-relay.brevo.com:2525`, STARTTLS - 2525 rather than 587 because many networks, including the dev machine's, block 587; override with `EMAIL_SMTP_PORT`). Sender identity is "Olive from Ogasela"
  &lt;support@ogasela.com&gt;.
- **`BrandedEmailTemplate.Render(...)`** (`Application/Notifications`) — the single shell
  every outbound email goes through: a card layout, "Ogasela" wordmark, brand green
  (`#0B6E4F`), a hidden preheader, an optional CTA button, and a footer pointing to
  `support@ogasela.com`. Table-based inline-CSS HTML (not modern CSS) because that's what
  actually renders correctly in Outlook. Add new email types by calling `Render` with a
  heading/paragraphs/CTA — don't hand-write new HTML.

**Current triggers.** `RequestOtpCommandHandler` sends the OTP via SMS after generating it.
`RegisterUserCommandHandler` sends a branded welcome email when the user registers with an
email address. Both sends are wrapped in try/catch and logged on failure without failing the
command — a delivery hiccup shouldn't block registration or force the client to see an error
when the code/account was in fact created; the client can retry `/otp/request` if the SMS
didn't arrive.

**Secrets — only in `.env`, never in `appsettings.json` or User Secrets.** Every credential
(`Termii:ApiKey`, `Email:SmtpKey`, JWT, AWS, payment, AI, social-login keys, ...) lives in the
gitignored `.env` at the repo root - copy `.env.example` and fill it in. `appsettings.json` only
has the non-secret config, with empty strings for the secret values. The same `.env` serves both
ways of running:
- **`dotnet run`** - `Api/Common/DotEnvConfiguration.cs` reads `.env` at startup and maps each
  name (e.g. `TERMII_API_KEY`) to its config key (`Termii:ApiKey`). It sits just above the
  appsettings files, so real environment variables and command-line settings still win. The project has no `UserSecretsId`; don't add one.
- **Docker** - `docker-compose.yml` maps the same names into the container's environment
  (`docker compose up` fails fast if a required one is missing). `.env` itself is kept out of
  the image by `.dockerignore`.

Adding a credential: put it in `.env` (and a blank entry in `.env.example`), then add the name
to both `DotEnvConfiguration.Map` and `docker-compose.yml`.

**Testing note.** `AccountsApiFactory` replaces `ISmsSender`/`IEmailSender` with no-op fakes
(`Ogasela.IntegrationTests/Accounts/NoOpNotificationSenders.cs`) via `ConfigureTestServices` —
integration tests must never call the real Termii/Brevo APIs.

## Conventions

- Nullable reference types are enabled solution-wide via `Directory.Build.props`.
- Application-layer handlers return `Ogasela.Shared.Result` / `Result<T>` rather than
  throwing for expected failure cases (validation, not-found, business-rule violations).
- MediatR is registered from `Ogasela.Application`'s `DependencyInjection.AddApplication()`
  and wired into the API's DI container in `Program.cs`.
- Connection strings live in `appsettings.Development.json` locally and are overridden via
  environment variables (`ConnectionStrings__Default`, `ConnectionStrings__Redis`, etc.) in
  Docker/CI.
- Redis is registered in `Ogasela.Infrastructure`'s `AddInfrastructure()` as the standard
  `IDistributedCache` (via `Microsoft.Extensions.Caching.StackExchangeRedis`), keyed off the
  `Redis` connection string. Use `IDistributedCache` for caching needs rather than talking to
  `StackExchange.Redis` directly, unless a module needs Redis-specific features (pub/sub,
  sorted sets) that `IDistributedCache` doesn't expose.

## Running locally

```bash
docker compose up --build
```

This starts the API (`http://localhost:8080`), Postgres 16, and Redis 7. In `Development`,
the API exposes Scalar's interactive API docs at `/scalar/v1` and a health check at
`/health` that verifies the database connection.

## Testing

```bash
dotnet test
```

`Ogasela.IntegrationTests` spins up real Postgres and Redis containers via Testcontainers
(`Testcontainers.PostgreSql`, `Testcontainers.Redis`) — Docker must be running locally to
execute it. The Accounts flow tests (`Accounts/AuthFlowTests.cs`) additionally boot the app
via `WebApplicationFactory<Program>`; connection strings are injected with
`IWebHostBuilder.UseSetting(...)` in `AccountsApiFactory`, not `ConfigureAppConfiguration`,
because `Program.cs` reads `builder.Configuration.GetConnectionString(...)` before
`builder.Build()` runs and `ConfigureAppConfiguration` callbacks are wired in too late for the
minimal-hosting model to see them at that point.

**Ad checkout (posting an ad).** A listing is a `Draft` from the app's first "Continue" until its
plan is paid for - drafts may be partial (empty description, no photos) and hold up to
`Listing.MaxPhotoCount` (30) photos whatever the plan; on submission (`ListingPublishService.SubmitAsync`)
photos beyond the plan's `PhotoLimit` are dropped from the end, keeping the cover - not at checkout start,
so an abandoned card payment never loses photos. The dropped files are deleted from storage by
`ListingMediaCleanup` - only after the save that committed the trim (callers of `SubmitAsync` call
`FlushDroppedMediaAsync` after saving), only for URLs our storage uploaded, and never while another
listing still uses the URL. Best effort: a failed delete is logged, never fails the submission. On S3
this needs `s3:DeleteObject` on the `listings/` prefix. `POST
/listings/{id}/checkout` (`Application/Listings/Checkout/ListingCheckout.cs`) runs every publish
rule except payment first (`ListingPublishService.CheckEligibilityAsync`: verified seller, title +
description + at least one photo, Free-plan rules), so money is never taken
for an ad that can't be submitted. Free and Wallet settle in that call; Card creates a Pending
`PlanPurchase` transaction linked to the listing and returns the gateway URL. A card payment is
settled by `ListingPaymentSettlement` - called both by the payment webhook and by `POST
.../checkout/{reference}/confirm` (the app calls it when the payment page closes, since webhooks
can lag and never reach a local API). Settlement is idempotent, and if a confirmed payment can't
submit the listing (already paid another way, no longer eligible, underpaid) it's refunded to the
payer's card/account - it's never lost. A settled listing goes to `PendingReview` (or live if
`Listings:RequireApproval` is off); a rejection refunds the plan. Every plan refund goes through
`PlanChargeRefunder` (`Application/Payments`): card payments back through the gateway (claimed under
the Transaction's xmin first, put back if the gateway refuses - a rejection then fails with 502 and
the ad stays in review), and only pre-existing wallet-paid charges back to the wallet. The wallet is
switched off in the app for now (no top-ups, balance or wallet payment option), so don't add new
code that credits it.

**Office pickup.** A buyer pays Ogasela an ad's price plus 3% (`OfficePickupRules.ServiceFeeRate`),
the seller drops the item at an `OfficeBranch`, staff inspect it, and the buyer collects it (or it's
delivered). Code lives in `Domain/OfficePickups`, `Application/OfficePickups` (app side) and
`Application/Admin/OfficePickups` (Control Portal). It's offered only when the ad's category or its
parent has `Category.IsOfficePickupEligible` (set in the portal), the ad is live and has a price -
`OfficePickupRules.IsAvailableAsync`, surfaced as `ListingDetailResponse.OfficePickupAvailable`.
The payment is **not** a wallet `Transaction` (buyers have no seller wallet): it's tracked on
`OfficePickupRequest.PaymentReference`, started with `IPaymentGateway.InitializeCustomerTransactionAsync`
and settled by `OfficePickupPaymentSettlement` from either the webhook (which falls back to it when
no Transaction matches the reference) or `POST /office-pickups/{id}/confirm-payment`. Settlement
emails the buyer a receipt and notifies both parties. Each staff step adds an `OfficePickupActivity`
(the timeline the app shows) and notifies via `OfficePickupNotifier` (inbox entry with
`OfficePickupId` + push using the payload's own title/body) - transactional, so not subject to the
notification-preference toggles. A refund (`officepickup.refund`) pays back the total less 1% of
the item price (`CancellationFeeRate`) through `ProcessPartialRefundAsync`; the request is claimed
first under its xmin token so two staff can't refund it twice. The 3%/1% figures are quoted in the
app and in both Terms of Service (app `(modals)/terms.tsx`, website `terms-and-conditions`) - change
them together. See `IntegrationTests/Payments/OfficePickupTests.cs`.

**Verified sign-in.** `User.EmailVerifiedAt`/`PhoneVerifiedAt` record proven ownership. Every
consumer sign-in must go through a verified email or phone: `LoginCommandHandler` sends the code to
the identifier signed in with, plus the other contact only if it's verified. If the identifier
isn't verified, the code goes there alone, the response has `VerificationRequired: true`, and
`VerifyLoginOtpCommandHandler` marks it verified on success. Codes are keyed by that identifier
(`LoginOtpKey`: bare phone, or `email:` for email). Contacts also become verified through
`otp/verify` (phone), an approved contact change (proven by code before filing), and social sign-in
when the provider vouches for the email. Note the master OTP passes these checks too. See
`IntegrationTests/Accounts/ContactVerificationTests.cs`.

**Waiting list & testers.** The website's "Join waiting list" (shown instead of "Get the app" until
the site's `NEXT_APP_LIVE=true`) and "Join our testers" forms post to `POST /marketing/waitlist` and
`POST /marketing/testers`. Both go through `SubmitInterestSignupCommand`
(`Application/Marketing/SubmitInterestSignup`) into one `InterestSignup` table, keyed by
(`Kind`, lower-cased `Email`); a repeat signup is a silent success, and a new one gets a best-effort
branded confirmation email. Both forms ask for the phone's OS (`DevicePlatform`, Android or IOS - required), so launch emails can
point at the right store. `Consent` must be true - it's the NDPA lawful basis for keeping the row
(`ConsentedAt`). Staff manage them on the Control Portal's Waitlist & Testers page
(`Admin/InterestSignups`, `/admin/interest-signups`): `marketing.interest.view` to read,
`marketing.interest.manage` to change status/notes, export CSV (audited, formula-safe) or delete. Delete
is a hard delete for erasure requests, and its audit entry deliberately omits the name and email.

**Testimonials.** The homepage "Word on the street" section (OgaselaWebClient) reads `GET
/testimonials` - published ones only, newest first, at most `TestimonialHandlers.MaxPublished` (6).
Staff add, edit, publish/unpublish and delete them on the Control Portal's Testimonials page
(`/admin/testimonials`, `marketing.testimonials.manage`). Creating one requires `ConsentConfirmed:
true` - staff vouch the person is a real user who agreed to be quoted, stamped as `ConsentConfirmedAt`.
Every change is audited; the delete entry omits the quote and name, since deleting is usually the
person withdrawing consent. Code: `Application/Marketing/Testimonials`, `TestimonialsController`.

**Deleted accounts keep their chats.** Deleting an account (`DELETE /me`, which runs the same
`EraseUserDataCommand` as the portal's Data Erasure) anonymizes the user but never removes their
`Conversation`s or `Message`s - everyone they did business with keeps the chat for reference. Their
ads are taken down, not deleted, so the chat can still show what it was about. Don't add a cascade
or cleanup that deletes chats on erasure; `AdminUsersTests` covers both deletion paths. Erasure stamps
`User.DeletedAt` (backfilled for older erasures from their `User.DataErasure` audit entries). Such a chat is
closed: `ConversationResponse.IsOtherParticipantDeleted` is true, sending fails with
`Messaging.ParticipantDeleted` (409), and no new chat can be started with that account. The website and
app show "This account was deleted" in place of the message box.

**Control Portal notifications.** `GET /admin/pending-actions` (`Application/Admin/PendingActions`) counts
the work waiting in each queue the caller can act on - the same role/permission the action endpoint
needs (moderation queues: Moderator or SuperAdmin; the rest: their `perm:` key). The portal polls it
every minute for the top-bar bell, sidebar badges, tab title and optional desktop alerts. Adding a queue:
a key in `PendingActionKeys` + a count here, and its label/link in the portal's `lib/pendingActions.tsx`.
