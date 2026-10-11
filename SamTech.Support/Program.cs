using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
var connectionString = builder.Configuration.GetConnectionString("SupportDatabase")
    ?? Environment.GetEnvironmentVariable("SUPPORT_DATABASE");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Configure ConnectionStrings:SupportDatabase or SUPPORT_DATABASE for the separate SAM Tech support database.");
var samDentalIntegrationKey = builder.Configuration["SamDentalIntegration:ApiKey"]
    ?? Environment.GetEnvironmentVariable("SAM_DENTAL_INTEGRATION_KEY");

builder.Services.AddSingleton(new SupportRepository(connectionString));
builder.Services.AddRateLimiter(options => options.AddPolicy("support-auth", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 8,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        })));
var app = builder.Build();
var repository = app.Services.GetRequiredService<SupportRepository>();
await repository.InitializeAsync(app.Lifetime.ApplicationStopping);

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseRateLimiter();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "SAM Tech Support" }));

app.MapPost("/api/auth/register", async (RegisterRequest request, HttpContext context) =>
{
    var validationError = ValidateRegistration(request);
    if (validationError is not null) return Results.BadRequest(new { message = validationError });
    try
    {
        await repository.RegisterOrganizationAsync(request, context.RequestAborted);
        return Results.Created("/support.html", new { message = "درخواست ساخت حساب ثبت شد. پس از تأیید پشتیبانی می‌توانید وارد شوید." });
    }
    catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number is 2601 or 2627)
    {
        return Results.Conflict(new { message = "این ایمیل قبلاً ثبت شده است." });
    }
}).RequireRateLimiting("support-auth");

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { message = "ایمیل و گذرواژه را وارد کنید." });

    var login = await repository.LoginAsync(request.Email, request.Password, context.RequestAborted);
    if (login.Status == LoginStatus.Invalid)
        return Results.Unauthorized();
    if (login.Status == LoginStatus.Pending)
        return Results.Json(new { message = "حساب شما هنوز توسط تیم پشتیبانی تأیید نشده است." }, statusCode: StatusCodes.Status403Forbidden);

    context.Response.Cookies.Append("samtech_support", login.Token!, new CookieOptions
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = DateTimeOffset.UtcNow.AddHours(12)
    });
    return Results.Ok(new { user = login.User });
}).RequireRateLimiting("support-auth");

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await repository.LogoutAsync(context.Request.Cookies["samtech_support"], context.RequestAborted);
    context.Response.Cookies.Delete("samtech_support", new CookieOptions { Path = "/", SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, HttpOnly = true });
    return Results.Ok(new { message = "خارج شدید." });
});

app.MapGet("/api/auth/me", async (HttpContext context) =>
{
    var user = await repository.GetCurrentUserAsync(context.Request.Cookies["samtech_support"], context.RequestAborted);
    return user is null ? Results.Unauthorized() : Results.Ok(new { user });
});

app.MapGet("/api/tickets", async (HttpContext context) =>
{
    var user = await RequireUser(repository, context);
    return user is null ? Results.Unauthorized() : Results.Ok(await repository.ListTicketsAsync(user, context.RequestAborted));
});

app.MapPost("/api/tickets", async (CreateTicketRequest request, HttpContext context) =>
{
    var user = await RequireCustomer(repository, context);
    if (user is null) return Results.Unauthorized();
    var validationError = ValidateTicket(request);
    if (validationError is not null) return Results.BadRequest(new { message = validationError });
    var ticket = await repository.CreateTicketAsync(user, request, context.RequestAborted);
    return Results.Created($"/api/tickets/{ticket.TicketNumber}", ticket);
});

app.MapPost("/api/integrations/sam-dental/tickets", async (SamDentalTicketRequest request, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(samDentalIntegrationKey) || samDentalIntegrationKey.Length < 32)
        return Results.Problem("ارتباط سام‌دنتال برای پشتیبانی سایت پیکربندی نشده است.", statusCode: StatusCodes.Status503ServiceUnavailable);

    var suppliedKey = context.Request.Headers["X-SamTech-Integration-Key"].ToString();
    if (!MatchesIntegrationKey(suppliedKey, samDentalIntegrationKey))
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(request.CustomerEmail) || !new EmailAddressAttribute().IsValid(request.CustomerEmail))
        return Results.BadRequest(new { message = "ایمیل حساب فعال پشتیبانی معتبر نیست." });

    var validationError = ValidateTicket(new CreateTicketRequest(
        "SAM DENTAL", request.Category, request.Priority, request.Subject, request.Description));
    if (validationError is not null) return Results.BadRequest(new { message = validationError });

    var customer = await repository.FindActiveCustomerByEmailAsync(request.CustomerEmail, context.RequestAborted);
    if (customer is null)
        return Results.Conflict(new { message = "برای این ایمیل حساب مشتریِ فعال در پشتیبانی سام‌تک پیدا نشد." });

    var ticket = await repository.CreateTicketAsync(customer,
        new CreateTicketRequest("SAM DENTAL", request.Category, request.Priority, request.Subject, request.Description),
        context.RequestAborted);
    return Results.Created($"/api/tickets/{ticket.TicketNumber}", new
    {
        ticket.TicketNumber,
        ticket.Subject,
        ticket.Status,
        ticket.CreatedAtUtc,
        Message = "تیکت در پشتیبانی سایت سام‌تک ثبت شد."
    });
}).RequireRateLimiting("support-auth");

app.MapGet("/api/tickets/{ticketNumber}", async (string ticketNumber, HttpContext context) =>
{
    var user = await RequireUser(repository, context);
    if (user is null) return Results.Unauthorized();
    var ticket = await repository.GetTicketAsync(user, ticketNumber, context.RequestAborted);
    return ticket is null ? Results.NotFound() : Results.Ok(ticket);
});

app.MapPost("/api/tickets/{ticketNumber}/messages", async (string ticketNumber, AddMessageRequest request, HttpContext context) =>
{
    var user = await RequireUser(repository, context);
    if (user is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Trim().Length > 4000)
        return Results.BadRequest(new { message = "متن پاسخ الزامی است و باید حداکثر ۴۰۰۰ نویسه باشد." });
    var result = await repository.AddMessageAsync(user, ticketNumber, request.Message.Trim(), context.RequestAborted);
    return result switch
    {
        TicketOperationStatus.NotFound => Results.NotFound(),
        TicketOperationStatus.Closed => Results.Conflict(new { message = "این تیکت بسته شده است." }),
        _ => Results.Ok(new { message = "پاسخ ثبت شد." })
    };
});

var admin = app.MapGroup("/api/admin");
admin.MapGet("/registrations", async (HttpContext context) =>
{
    var user = await RequireSupportAdmin(repository, context);
    return user is null ? Results.Unauthorized() : Results.Ok(await repository.ListPendingRegistrationsAsync(context.RequestAborted));
});
admin.MapPost("/users/{userId:int}/activate", async (int userId, HttpContext context) =>
{
    var user = await RequireSupportAdmin(repository, context);
    if (user is null) return Results.Unauthorized();
    return await repository.ActivateCustomerAsync(userId, context.RequestAborted)
        ? Results.Ok(new { message = "حساب مشتری فعال شد." })
        : Results.NotFound();
});
admin.MapPost("/agents", async (CreateAgentRequest request, HttpContext context) =>
{
    var user = await RequireSupportAdmin(repository, context);
    if (user is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length > 120 ||
        string.IsNullOrWhiteSpace(request.Email) || !new EmailAddressAttribute().IsValid(request.Email) ||
        request.Password is null || request.Password.Length < 10 || request.Password.Length > 128)
        return Results.BadRequest(new { message = "نام، ایمیل معتبر و گذرواژهٔ حداقل ۱۰ نویسه‌ای لازم است." });
    try
    {
        var agent = await repository.CreateSupportAgentAsync(request, context.RequestAborted);
        return Results.Created("/api/admin/agents", new { agent });
    }
    catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number is 2601 or 2627)
    {
        return Results.Conflict(new { message = "این ایمیل قبلاً ثبت شده است." });
    }
});
admin.MapPatch("/tickets/{ticketNumber}", async (string ticketNumber, UpdateTicketRequest request, HttpContext context) =>
{
    var user = await RequireSupportStaff(repository, context);
    if (user is null) return Results.Unauthorized();
    if (!SupportRepository.AllowedStatuses.Contains(request.Status))
        return Results.BadRequest(new { message = "وضعیت انتخاب‌شده معتبر نیست." });
    return await repository.UpdateTicketStatusAsync(ticketNumber, request.Status, context.RequestAborted)
        ? Results.Ok(new { message = "وضعیت تیکت به‌روز شد." })
        : Results.NotFound();
});

app.Run();

static async Task<SupportUser?> RequireUser(SupportRepository repository, HttpContext context) =>
    await repository.GetCurrentUserAsync(context.Request.Cookies["samtech_support"], context.RequestAborted);

static async Task<SupportUser?> RequireCustomer(SupportRepository repository, HttpContext context)
{
    var user = await RequireUser(repository, context);
    return user?.Role == "Customer" ? user : null;
}

static async Task<SupportUser?> RequireSupportAdmin(SupportRepository repository, HttpContext context)
{
    var user = await RequireUser(repository, context);
    return user?.Role == "SupportAdmin" ? user : null;
}

static async Task<SupportUser?> RequireSupportStaff(SupportRepository repository, HttpContext context)
{
    var user = await RequireUser(repository, context);
    return user?.Role is "SupportAdmin" or "SupportAgent" ? user : null;
}

static string? ValidateRegistration(RegisterRequest request)
{
    if (string.IsNullOrWhiteSpace(request.OrganizationName) || request.OrganizationName.Trim().Length > 160) return "نام مرکز را وارد کنید.";
    if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length > 120) return "نام و نام خانوادگی را وارد کنید.";
    if (string.IsNullOrWhiteSpace(request.Email) || !new EmailAddressAttribute().IsValid(request.Email)) return "ایمیل معتبر وارد کنید.";
    if (string.IsNullOrWhiteSpace(request.Phone) || request.Phone.Trim().Length > 40) return "شماره تماس را وارد کنید.";
    if (request.Password is null || request.Password.Length < 10 || request.Password.Length > 128) return "گذرواژه باید بین ۱۰ تا ۱۲۸ نویسه باشد.";
    return null;
}

static string? ValidateTicket(CreateTicketRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Trim().Length > 120) return "عنوان تیکت را وارد کنید (حداکثر ۱۲۰ نویسه).";
    if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 3000) return "شرح درخواست را وارد کنید (حداکثر ۳۰۰۰ نویسه).";
    if (!SupportRepository.AllowedCategories.Contains(request.Category)) return "دسته‌بندی معتبر نیست.";
    if (!SupportRepository.AllowedPriorities.Contains(request.Priority)) return "اولویت معتبر نیست.";
    if (request.Product != "SAM DENTAL") return "محصول انتخاب‌شده معتبر نیست.";
    return null;
}

static bool MatchesIntegrationKey(string supplied, string configured)
{
    if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(configured)) return false;
    var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
    return CryptographicOperations.FixedTimeEquals(suppliedHash, configuredHash);
}

public sealed record RegisterRequest(string OrganizationName, string FullName, string Email, string Phone, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record CreateTicketRequest(string Product, string Category, string Priority, string Subject, string Description);
public sealed record SamDentalTicketRequest(string CustomerEmail, string Category, string Priority, string Subject, string Description);
public sealed record AddMessageRequest(string Message);
public sealed record UpdateTicketRequest(string Status);
public sealed record CreateAgentRequest(string FullName, string Email, string Password);
