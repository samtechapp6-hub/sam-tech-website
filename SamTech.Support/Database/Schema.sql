IF OBJECT_ID(N'dbo.SupportOrganizations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupportOrganizations (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupportOrganizations PRIMARY KEY,
        Name NVARCHAR(160) NOT NULL,
        Phone NVARCHAR(40) NOT NULL,
        Status NVARCHAR(24) NOT NULL CONSTRAINT DF_SupportOrganizations_Status DEFAULT N'Pending',
        CreatedAtUtc DATETIME2(0) NOT NULL CONSTRAINT DF_SupportOrganizations_CreatedAtUtc DEFAULT SYSUTCDATETIME()
    );
END;

IF OBJECT_ID(N'dbo.SupportUsers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupportUsers (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupportUsers PRIMARY KEY,
        OrganizationId INT NULL,
        FullName NVARCHAR(120) NOT NULL,
        Email NVARCHAR(254) NOT NULL,
        PasswordHash NVARCHAR(300) NOT NULL,
        Role NVARCHAR(24) NOT NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_SupportUsers_IsActive DEFAULT 0,
        CreatedAtUtc DATETIME2(0) NOT NULL CONSTRAINT DF_SupportUsers_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_SupportUsers_Organizations FOREIGN KEY (OrganizationId) REFERENCES dbo.SupportOrganizations(Id)
    );
    CREATE UNIQUE INDEX UX_SupportUsers_Email ON dbo.SupportUsers(Email);
    CREATE INDEX IX_SupportUsers_OrganizationId ON dbo.SupportUsers(OrganizationId);
END;

IF OBJECT_ID(N'dbo.SupportSessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupportSessions (
        TokenHash CHAR(64) NOT NULL CONSTRAINT PK_SupportSessions PRIMARY KEY,
        UserId INT NOT NULL,
        ExpiresAtUtc DATETIME2(0) NOT NULL,
        CreatedAtUtc DATETIME2(0) NOT NULL CONSTRAINT DF_SupportSessions_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_SupportSessions_Users FOREIGN KEY (UserId) REFERENCES dbo.SupportUsers(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_SupportSessions_ExpiresAtUtc ON dbo.SupportSessions(ExpiresAtUtc);
END;

IF OBJECT_ID(N'dbo.SupportTickets', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupportTickets (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupportTickets PRIMARY KEY,
        TicketNumber NVARCHAR(24) NULL,
        OrganizationId INT NOT NULL,
        CreatedByUserId INT NOT NULL,
        Product NVARCHAR(80) NOT NULL,
        Category NVARCHAR(60) NOT NULL,
        Priority NVARCHAR(20) NOT NULL,
        Subject NVARCHAR(120) NOT NULL,
        Status NVARCHAR(32) NOT NULL CONSTRAINT DF_SupportTickets_Status DEFAULT N'جدید',
        CreatedAtUtc DATETIME2(0) NOT NULL CONSTRAINT DF_SupportTickets_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        UpdatedAtUtc DATETIME2(0) NOT NULL CONSTRAINT DF_SupportTickets_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_SupportTickets_Organizations FOREIGN KEY (OrganizationId) REFERENCES dbo.SupportOrganizations(Id),
        CONSTRAINT FK_SupportTickets_Creator FOREIGN KEY (CreatedByUserId) REFERENCES dbo.SupportUsers(Id)
    );
    CREATE UNIQUE INDEX UX_SupportTickets_TicketNumber ON dbo.SupportTickets(TicketNumber) WHERE TicketNumber IS NOT NULL;
    CREATE INDEX IX_SupportTickets_OrganizationUpdated ON dbo.SupportTickets(OrganizationId, UpdatedAtUtc DESC);
    CREATE INDEX IX_SupportTickets_StatusUpdated ON dbo.SupportTickets(Status, UpdatedAtUtc DESC);
END;

IF OBJECT_ID(N'dbo.SupportTicketMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupportTicketMessages (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupportTicketMessages PRIMARY KEY,
        TicketId BIGINT NOT NULL,
        SenderUserId INT NOT NULL,
        Message NVARCHAR(4000) NOT NULL,
        CreatedAtUtc DATETIME2(0) NOT NULL CONSTRAINT DF_SupportTicketMessages_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_SupportTicketMessages_Tickets FOREIGN KEY (TicketId) REFERENCES dbo.SupportTickets(Id) ON DELETE CASCADE,
        CONSTRAINT FK_SupportTicketMessages_Sender FOREIGN KEY (SenderUserId) REFERENCES dbo.SupportUsers(Id)
    );
    CREATE INDEX IX_SupportTicketMessages_TicketCreated ON dbo.SupportTicketMessages(TicketId, CreatedAtUtc);
END;
