using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

public sealed class SupportRepository(string connectionString)
{
    public static readonly HashSet<string> AllowedCategories = new(StringComparer.Ordinal)
    {
        "فنی و خطا", "راهنمایی", "حساب و مجوز", "پیشنهاد", "سایر"
    };

    public static readonly HashSet<string> AllowedPriorities = new(StringComparer.Ordinal)
    {
        "عادی", "فوری"
    };

    public static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "جدید", "در حال بررسی", "منتظر پاسخ مشتری", "حل‌شده", "بسته‌شده"
    };

    private SqlConnection OpenConnection() => new(connectionString);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Database", "Schema.sql");
        if (!File.Exists(schemaPath))
            throw new FileNotFoundException("Support database schema was not included in the application output.", schemaPath);

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var command = new SqlCommand(await File.ReadAllTextAsync(schemaPath, cancellationToken), connection))
            await command.ExecuteNonQueryAsync(cancellationToken);

        await SeedSupportAdminAsync(connection, cancellationToken);
        await using var cleanup = new SqlCommand("DELETE FROM dbo.SupportSessions WHERE ExpiresAtUtc <= SYSUTCDATETIME();", connection);
        await cleanup.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedSupportAdminAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var email = Environment.GetEnvironmentVariable("SUPPORT_ADMIN_EMAIL")?.Trim().ToLowerInvariant();
        var password = Environment.GetEnvironmentVariable("SUPPORT_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)) return;
        if (password.Length < 10) throw new InvalidOperationException("SUPPORT_ADMIN_PASSWORD must have at least 10 characters.");

        var passwordHash = HashPassword(password);
        const string sql = "IF NOT EXISTS (SELECT 1 FROM dbo.SupportUsers WHERE Email = @Email) " +
                           "INSERT dbo.SupportUsers (OrganizationId, FullName, Email, PasswordHash, Role, IsActive) " +
                           "VALUES (NULL, @FullName, @Email, @PasswordHash, N'SupportAdmin', 1);";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Email", SqlDbType.NVarChar, 254).Value = email;
        command.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = Environment.GetEnvironmentVariable("SUPPORT_ADMIN_NAME")?.Trim() is { Length: > 0 } name ? name : "مدیر پشتیبانی";
        command.Parameters.Add("@PasswordHash", SqlDbType.NVarChar, 300).Value = passwordHash;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RegisterOrganizationAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string insertOrganization = "INSERT dbo.SupportOrganizations (Name, Phone) OUTPUT INSERTED.Id VALUES (@Name, @Phone);";
            await using var organizationCommand = new SqlCommand(insertOrganization, connection, transaction);
            organizationCommand.Parameters.Add("@Name", SqlDbType.NVarChar, 160).Value = request.OrganizationName.Trim();
            organizationCommand.Parameters.Add("@Phone", SqlDbType.NVarChar, 40).Value = request.Phone.Trim();
            var organizationId = (int)(await organizationCommand.ExecuteScalarAsync(cancellationToken) ?? 0);

            const string insertUser = "INSERT dbo.SupportUsers (OrganizationId, FullName, Email, PasswordHash, Role, IsActive) " +
                                      "VALUES (@OrganizationId, @FullName, @Email, @PasswordHash, N'Customer', 0);";
            await using var userCommand = new SqlCommand(insertUser, connection, transaction);
            userCommand.Parameters.Add("@OrganizationId", SqlDbType.Int).Value = organizationId;
            userCommand.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = request.FullName.Trim();
            userCommand.Parameters.Add("@Email", SqlDbType.NVarChar, 254).Value = request.Email.Trim().ToLowerInvariant();
            userCommand.Parameters.Add("@PasswordHash", SqlDbType.NVarChar, 300).Value = HashPassword(request.Password);
            await userCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = "SELECT u.Id, u.OrganizationId, u.FullName, u.Email, u.Role, u.IsActive, u.PasswordHash, o.Name " +
                           "FROM dbo.SupportUsers u LEFT JOIN dbo.SupportOrganizations o ON o.Id = u.OrganizationId " +
                           "WHERE u.Email = @Email;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Email", SqlDbType.NVarChar, 254).Value = email.Trim().ToLowerInvariant();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || !VerifyPassword(password, reader.GetString(6)))
            return LoginResult.Invalid;

        if (!reader.GetBoolean(5)) return LoginResult.Pending;
        var user = ReadUser(reader, 7);
        await reader.CloseAsync();

        var token = NewToken();
        var tokenHash = HashToken(token);
        const string insertSession = "INSERT dbo.SupportSessions (TokenHash, UserId, ExpiresAtUtc) VALUES (@Hash, @UserId, @Expires);";
        await using var sessionCommand = new SqlCommand(insertSession, connection);
        sessionCommand.Parameters.Add("@Hash", SqlDbType.Char, 64).Value = tokenHash;
        sessionCommand.Parameters.Add("@UserId", SqlDbType.Int).Value = user.Id;
        sessionCommand.Parameters.Add("@Expires", SqlDbType.DateTime2).Value = DateTime.UtcNow.AddHours(12);
        await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
        return new LoginResult(LoginStatus.Success, token, user);
    }

    public async Task<SupportUser?> GetCurrentUserAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = "SELECT u.Id, u.OrganizationId, u.FullName, u.Email, u.Role, o.Name " +
                           "FROM dbo.SupportSessions s JOIN dbo.SupportUsers u ON u.Id = s.UserId " +
                           "LEFT JOIN dbo.SupportOrganizations o ON o.Id = u.OrganizationId " +
                           "WHERE s.TokenHash = @Hash AND s.ExpiresAtUtc > SYSUTCDATETIME() AND u.IsActive = 1;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Hash", SqlDbType.Char, 64).Value = HashToken(token);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader, 5) : null;
    }

    public async Task LogoutAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("DELETE dbo.SupportSessions WHERE TokenHash = @Hash;", connection);
        command.Parameters.Add("@Hash", SqlDbType.Char, 64).Value = HashToken(token);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TicketSummary>> ListTicketsAsync(SupportUser user, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var isSupport = IsSupportRole(user.Role);
        const string sql = "SELECT t.TicketNumber, t.Product, t.Category, t.Priority, t.Subject, t.Status, " +
                           "t.CreatedAtUtc, t.UpdatedAtUtc, o.Name, u.FullName " +
                           "FROM dbo.SupportTickets t " +
                           "JOIN dbo.SupportOrganizations o ON o.Id = t.OrganizationId " +
                           "JOIN dbo.SupportUsers u ON u.Id = t.CreatedByUserId " +
                           "WHERE (@IsAdmin = 1 OR t.OrganizationId = @OrganizationId) " +
                           "ORDER BY t.UpdatedAtUtc DESC;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@IsAdmin", SqlDbType.Bit).Value = isSupport;
        command.Parameters.Add("@OrganizationId", SqlDbType.Int).Value = (object?)user.OrganizationId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TicketSummary>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadSummary(reader));
        return result;
    }

    public async Task<SupportUser?> FindActiveCustomerByEmailAsync(string email, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = "SELECT u.Id, u.OrganizationId, u.FullName, u.Email, u.Role, o.Name " +
                           "FROM dbo.SupportUsers u JOIN dbo.SupportOrganizations o ON o.Id = u.OrganizationId " +
                           "WHERE u.Email = @Email AND u.Role = N'Customer' AND u.IsActive = 1 AND o.Status = N'Active';";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Email", SqlDbType.NVarChar, 254).Value = email.Trim().ToLowerInvariant();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader, 5) : null;
    }

    public async Task<TicketSummary> CreateTicketAsync(SupportUser user, CreateTicketRequest request, CancellationToken cancellationToken)
    {
        if (user.OrganizationId is null) throw new InvalidOperationException("Customer user has no organization.");
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string insert = "INSERT dbo.SupportTickets (OrganizationId, CreatedByUserId, Product, Category, Priority, Subject) " +
                                  "OUTPUT INSERTED.Id VALUES (@OrganizationId, @UserId, @Product, @Category, @Priority, @Subject);";
            await using var command = new SqlCommand(insert, connection, transaction);
            command.Parameters.Add("@OrganizationId", SqlDbType.Int).Value = user.OrganizationId.Value;
            command.Parameters.Add("@UserId", SqlDbType.Int).Value = user.Id;
            command.Parameters.Add("@Product", SqlDbType.NVarChar, 80).Value = request.Product;
            command.Parameters.Add("@Category", SqlDbType.NVarChar, 60).Value = request.Category;
            command.Parameters.Add("@Priority", SqlDbType.NVarChar, 20).Value = request.Priority;
            command.Parameters.Add("@Subject", SqlDbType.NVarChar, 120).Value = request.Subject.Trim();
            var id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
            var ticketNumber = $"ST-{DateTime.UtcNow:yy}-{id:000000}";

            await using (var update = new SqlCommand("UPDATE dbo.SupportTickets SET TicketNumber = @Number WHERE Id = @Id;", connection, transaction))
            {
                update.Parameters.Add("@Number", SqlDbType.NVarChar, 24).Value = ticketNumber;
                update.Parameters.Add("@Id", SqlDbType.BigInt).Value = id;
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var message = new SqlCommand("INSERT dbo.SupportTicketMessages (TicketId, SenderUserId, Message) VALUES (@TicketId, @UserId, @Message);", connection, transaction))
            {
                message.Parameters.Add("@TicketId", SqlDbType.BigInt).Value = id;
                message.Parameters.Add("@UserId", SqlDbType.Int).Value = user.Id;
                message.Parameters.Add("@Message", SqlDbType.NVarChar, 4000).Value = request.Description.Trim();
                await message.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new TicketSummary(ticketNumber, request.Product, request.Category, request.Priority, request.Subject.Trim(), "جدید", DateTime.UtcNow, DateTime.UtcNow, "", user.FullName);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<TicketDetail?> GetTicketAsync(SupportUser user, string ticketNumber, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        const string ticketSql = "SELECT t.Id, t.TicketNumber, t.Product, t.Category, t.Priority, t.Subject, t.Status, " +
                                 "t.CreatedAtUtc, t.UpdatedAtUtc, o.Name, u.FullName, t.OrganizationId " +
                                 "FROM dbo.SupportTickets t JOIN dbo.SupportOrganizations o ON o.Id = t.OrganizationId " +
                                 "JOIN dbo.SupportUsers u ON u.Id = t.CreatedByUserId WHERE t.TicketNumber = @Number;";
        long id;
        int organizationId;
        TicketSummary summary;
        await using (var command = new SqlCommand(ticketSql, connection))
        {
            command.Parameters.Add("@Number", SqlDbType.NVarChar, 24).Value = ticketNumber;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            organizationId = reader.GetInt32(11);
            if (!IsSupportRole(user.Role) && user.OrganizationId != organizationId) return null;
            id = reader.GetInt64(0);
            summary = new TicketSummary(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), AsUtc(reader.GetDateTime(7)), AsUtc(reader.GetDateTime(8)), reader.GetString(9), reader.GetString(10));
        }

        const string messagesSql = "SELECT m.Id, m.Message, m.CreatedAtUtc, u.FullName, u.Role " +
                                  "FROM dbo.SupportTicketMessages m JOIN dbo.SupportUsers u ON u.Id = m.SenderUserId " +
                                  "WHERE m.TicketId = @Id ORDER BY m.CreatedAtUtc, m.Id;";
        await using var messageCommand = new SqlCommand(messagesSql, connection);
        messageCommand.Parameters.Add("@Id", SqlDbType.BigInt).Value = id;
        await using var messagesReader = await messageCommand.ExecuteReaderAsync(cancellationToken);
        var messages = new List<TicketMessage>();
        while (await messagesReader.ReadAsync(cancellationToken))
            messages.Add(new TicketMessage(messagesReader.GetInt64(0), messagesReader.GetString(1), AsUtc(messagesReader.GetDateTime(2)), messagesReader.GetString(3), IsSupportRole(messagesReader.GetString(4))));
        return new TicketDetail(summary, messages);
    }

    public async Task<TicketOperationStatus> AddMessageAsync(SupportUser user, string ticketNumber, string messageText, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string find = "SELECT Id, OrganizationId, Status FROM dbo.SupportTickets WITH (UPDLOCK, ROWLOCK) WHERE TicketNumber = @Number;";
            long id = 0;
            int organizationId = 0;
            string? status = null;
            await using (var findCommand = new SqlCommand(find, connection, transaction))
            {
                findCommand.Parameters.Add("@Number", SqlDbType.NVarChar, 24).Value = ticketNumber;
                await using var reader = await findCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    id = reader.GetInt64(0);
                    organizationId = reader.GetInt32(1);
                    status = reader.GetString(2);
                }
            }
            if (status is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return TicketOperationStatus.NotFound;
            }
            if (!IsSupportRole(user.Role) && user.OrganizationId != organizationId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return TicketOperationStatus.NotFound;
            }
            if (status == "بسته‌شده")
            {
                await transaction.RollbackAsync(cancellationToken);
                return TicketOperationStatus.Closed;
            }

            await using (var insert = new SqlCommand("INSERT dbo.SupportTicketMessages (TicketId, SenderUserId, Message) VALUES (@Id, @UserId, @Message);", connection, transaction))
            {
                insert.Parameters.Add("@Id", SqlDbType.BigInt).Value = id;
                insert.Parameters.Add("@UserId", SqlDbType.Int).Value = user.Id;
                insert.Parameters.Add("@Message", SqlDbType.NVarChar, 4000).Value = messageText;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            var nextStatus = IsSupportRole(user.Role) ? "منتظر پاسخ مشتری" : "در حال بررسی";
            await using (var update = new SqlCommand("UPDATE dbo.SupportTickets SET Status = @Status, UpdatedAtUtc = SYSUTCDATETIME() WHERE Id = @Id;", connection, transaction))
            {
                update.Parameters.Add("@Status", SqlDbType.NVarChar, 32).Value = nextStatus;
                update.Parameters.Add("@Id", SqlDbType.BigInt).Value = id;
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return TicketOperationStatus.Success;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<PendingRegistration>> ListPendingRegistrationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = "SELECT u.Id, o.Name, u.FullName, u.Email, o.Phone, u.CreatedAtUtc " +
                           "FROM dbo.SupportUsers u JOIN dbo.SupportOrganizations o ON o.Id = u.OrganizationId " +
                           "WHERE u.Role = N'Customer' AND u.IsActive = 0 ORDER BY u.CreatedAtUtc;";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PendingRegistration>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new PendingRegistration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), AsUtc(reader.GetDateTime(5))));
        return result;
    }

    public async Task<bool> ActivateCustomerAsync(int userId, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string sql = "UPDATE dbo.SupportUsers SET IsActive = 1 WHERE Id = @Id AND Role = N'Customer'; " +
                               "UPDATE o SET Status = N'Active' FROM dbo.SupportOrganizations o " +
                               "JOIN dbo.SupportUsers u ON u.OrganizationId = o.Id WHERE u.Id = @Id AND u.Role = N'Customer'; " +
                               "SELECT @@ROWCOUNT;";
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = userId;
            var updated = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return updated > 0;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<SupportUser> CreateSupportAgentAsync(CreateAgentRequest request, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = "INSERT dbo.SupportUsers (OrganizationId, FullName, Email, PasswordHash, Role, IsActive) " +
                           "OUTPUT INSERTED.Id, INSERTED.FullName, INSERTED.Email " +
                           "VALUES (NULL, @FullName, @Email, @PasswordHash, N'SupportAgent', 1);";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@FullName", SqlDbType.NVarChar, 120).Value = request.FullName.Trim();
        command.Parameters.Add("@Email", SqlDbType.NVarChar, 254).Value = request.Email.Trim().ToLowerInvariant();
        command.Parameters.Add("@PasswordHash", SqlDbType.NVarChar, 300).Value = HashPassword(request.Password);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new SupportUser(reader.GetInt32(0), null, reader.GetString(1), reader.GetString(2), "SupportAgent", null);
    }

    public async Task<bool> UpdateTicketStatusAsync(string ticketNumber, string status, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = "UPDATE dbo.SupportTickets SET Status = @Status, UpdatedAtUtc = SYSUTCDATETIME() WHERE TicketNumber = @Number; SELECT @@ROWCOUNT;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Status", SqlDbType.NVarChar, 32).Value = status;
        command.Parameters.Add("@Number", SqlDbType.NVarChar, 24).Value = ticketNumber;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static SupportUser ReadUser(SqlDataReader reader, int organizationNameIndex) => new(
        reader.GetInt32(0),
        reader.IsDBNull(1) ? null : reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(organizationNameIndex) ? null : reader.GetString(organizationNameIndex));

    private static TicketSummary ReadSummary(SqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), AsUtc(reader.GetDateTime(6)), AsUtc(reader.GetDateTime(7)), reader.GetString(8), reader.GetString(9));

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static bool IsSupportRole(string role) => role is "SupportAdmin" or "SupportAgent";

    private static string HashPassword(string password)
    {
        const int iterations = 210_000;
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"v1${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations)) return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed record SupportUser(int Id, int? OrganizationId, string FullName, string Email, string Role, string? Organization);
public sealed record TicketSummary(string TicketNumber, string Product, string Category, string Priority, string Subject, string Status, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, string Organization, string CreatedBy);
public sealed record TicketMessage(long Id, string Message, DateTime CreatedAtUtc, string SenderName, bool IsSupport);
public sealed record TicketDetail(TicketSummary Ticket, IReadOnlyList<TicketMessage> Messages);
public sealed record PendingRegistration(int UserId, string Organization, string FullName, string Email, string Phone, DateTime CreatedAtUtc);
public enum TicketOperationStatus { Success, NotFound, Closed }
public enum LoginStatus { Success, Invalid, Pending }
public sealed record LoginResult(LoginStatus Status, string? Token = null, SupportUser? User = null)
{
    public static LoginResult Invalid => new(LoginStatus.Invalid);
    public static LoginResult Pending => new(LoginStatus.Pending);
}
