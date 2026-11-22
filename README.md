# Improvising Fugue — API

ASP.NET Core (.NET 10) Web API for the Improvising Fugue learning app: accounts, curriculum, lesson progress, XP/streaks, spaced repetition and Stripe subscriptions.

## Stack

- ASP.NET Core controllers, JWT bearer auth with rotating refresh tokens (HttpOnly cookie)
- EF Core + SQLite (swap the provider in `Program.cs` for SQL Server/Postgres later)
- BCrypt password hashing
- Stripe.net for Checkout, Customer Portal and webhooks
- xUnit tests in `Tests/`

## Layout

```
Controllers/   HTTP endpoints (all under /api)
Services/      Business logic: auth, curriculum, lessons, grading, reviews, billing
Models/        Request/response records
Domain/        EF entities
Data/          DbContext and the curriculum seeder
Content/       Curriculum JSON (one file per chapter)
Migrations/    EF Core migrations
Tests/         xUnit test project
```

## Running

```powershell
dotnet run --launch-profile http
```

The API listens on `http://localhost:5008`. On startup it applies migrations to `improvising-fugue.db` and seeds every file in `Content/Curriculum`. OpenAPI is at `/openapi/v1.json` in Development.

```powershell
dotnet test ImprovisingFugue.slnx
```

## Configuration

Settings come from `appsettings.json`, `appsettings.Development.json`, user secrets or environment variables (`Section__Key`).

| Key | Purpose |
| --- | --- |
| `ConnectionStrings__Default` | SQLite connection string |
| `Jwt__SigningKey` | HMAC key, 32+ chars. Required outside Development |
| `App__FrontendUrl` | Used for Stripe redirect URLs |
| `Stripe__SecretKey` | `sk_test_…` / `sk_live_…` |
| `Stripe__PremiumPriceId` | Recurring Price id for Premium |
| `Stripe__WebhookSecret` | `whsec_…` for the webhook endpoint |
| `Billing__EnableDevPremiumToggle` | Development only: exposes `POST /api/billing/dev/premium` |
| `Cors__Origins` | Only needed when the frontend is not using the dev proxy |

### Stripe setup

Without Stripe keys everything works except checkout and the portal, which return `503 billing_not_configured`. In Development you can toggle Premium from the Account page instead.

1. In the Stripe dashboard (test mode) create a Product with a recurring Price. Copy the `price_…` id. The price amount is read from Stripe; nothing is hardcoded.
2. Enable the Customer Portal (Settings → Billing → Customer portal).
3. Store the keys as user secrets:
   ```powershell
   dotnet user-secrets init
   dotnet user-secrets set "Stripe:SecretKey" "sk_test_..."
   dotnet user-secrets set "Stripe:PremiumPriceId" "price_..."
   ```
4. Forward webhooks locally with the Stripe CLI and save the printed secret:
   ```powershell
   stripe listen --forward-to localhost:5008/api/billing/webhook
   dotnet user-secrets set "Stripe:WebhookSecret" "whsec_..."
   ```

Handled events: `checkout.session.completed`, `customer.subscription.created|updated|deleted|paused|resumed`, `invoice.paid`, `invoice.payment_succeeded`, `invoice.payment_failed`. Each event re-fetches the subscription from Stripe and is recorded by id so redeliveries are ignored.

## Curriculum content

Each `Content/Curriculum/*.json` file is a chapter. The seeder upserts by slug/key, so editing a file and restarting updates content without touching user progress. A lesson's prerequisite defaults to the lesson before it (across chapters); set `prerequisites` to override. Set `isPremium` on a chapter to gate it.

Exercise types: `info`, `multipleChoice`, `ordering`, `keyboard`. Answers live in `answer` and are never sent to the client. `chapter-02-preview.json` is placeholder content used to exercise Premium gating.

## Learning rules

- First completion awards the lesson's XP, replays award a quarter (min 2), and a first perfect score adds a 5 XP mastery bonus.
- Score uses the first answer to each question; wrong answers are retried in the lesson but don't change the score.
- Completing a lesson schedules its review cards for the start of the user's next local day.
- Reviews use a simplified SM-2 (`Services/ReviewScheduler.cs`) behind `IReviewScheduler`, so FSRS can replace it later. Practice reviews are logged but don't reschedule.
- A streak counts any XP-earning activity per local day (user time zone).
