namespace Kanban.Infrastructure.Persistence;

public static class BusinessSecurityV1
{
    public static readonly string[] Tables =
    [
        "Company", "Membership", "Team", "Board", "WorkItem", "CompanyGroup", "GroupMember",
        "MemberGrant", "GroupGrant", "Invitation", "ClientApproval", "ConnectionGrant",
        "ConnectionTeam", "AuditEvent", "MutationReceipt"
    ];

    public static IEnumerable<string> MigrationStatements()
    {
        yield return "CREATE USER [kanban_directory] WITHOUT LOGIN;";
        yield return """
            CREATE FUNCTION [app].[CompanyScope](@CompanyId uniqueidentifier)
            RETURNS TABLE WITH SCHEMABINDING AS RETURN
            SELECT 1 AS allowed WHERE @CompanyId = TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N'CompanyId'));
            """;
        yield return """
            CREATE FUNCTION [app].[DirectoryScope](@CompanyId uniqueidentifier)
            RETURNS TABLE WITH SCHEMABINDING AS RETURN
            SELECT 1 AS allowed WHERE @CompanyId = TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N'CompanyId'))
                OR USER_NAME() = N'kanban_directory';
            """;
        foreach (var table in Tables)
        {
            var function = table is "Company" or "Membership" or "Invitation" ? "DirectoryScope" : "CompanyScope";
            yield return $"""
                CREATE SECURITY POLICY [app].[{table}Policy]
                ADD FILTER PREDICATE [app].[{function}]([CompanyId]) ON [app].[{table}],
                ADD BLOCK PREDICATE [app].[{function}]([CompanyId]) ON [app].[{table}] AFTER INSERT,
                ADD BLOCK PREDICATE [app].[{function}]([CompanyId]) ON [app].[{table}] AFTER UPDATE
                WITH (STATE = ON, SCHEMABINDING = ON);
                """;
        }
        yield return """
            GRANT SELECT, INSERT, UPDATE ON OBJECT::[bootstrap].[Employee] TO [kanban_directory];
            GRANT SELECT, INSERT ON OBJECT::[bootstrap].[CompanyCreationReceipt] TO [kanban_directory];
            GRANT SELECT, INSERT ON OBJECT::[app].[Company] TO [kanban_directory];
            GRANT SELECT, INSERT ON OBJECT::[app].[Membership] TO [kanban_directory];
            GRANT SELECT ([TokenHash], [CompanyId]) ON OBJECT::[app].[Invitation] TO [kanban_directory];
            GRANT INSERT ON OBJECT::[app].[AuditEvent] TO [kanban_directory];
            """;
        yield return """
            CREATE PROCEDURE [bootstrap].[ResolveEmployee]
                @DirectoryId uniqueidentifier, @ObjectId uniqueidentifier, @Name nvarchar(512), @Email nvarchar(320)
            WITH EXECUTE AS 'kanban_directory'
            AS
            BEGIN
                SET NOCOUNT ON; SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                DECLARE @lock int;
                DECLARE @resource nvarchar(255) = N'employee:' + CONVERT(nvarchar(36), @DirectoryId) + N':' + CONVERT(nvarchar(36), @ObjectId);
                EXEC @lock = sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
                IF @lock < 0 THROW 51003, 'Identity resolution is busy.', 1;
                DECLARE @id uniqueidentifier = (SELECT [Id] FROM [bootstrap].[Employee]
                    WHERE [DirectoryId]=@DirectoryId AND [ObjectId]=@ObjectId);
                IF @id IS NULL
                BEGIN
                    IF @Email IS NULL THROW 51003, 'A browser profile is required.', 1;
                    SET @id=NEWID();
                    INSERT [bootstrap].[Employee] ([Id],[DirectoryId],[ObjectId],[Name],[Email])
                    VALUES (@id,@DirectoryId,@ObjectId,@Name,@Email);
                END
                ELSE IF @Email IS NOT NULL
                    UPDATE [bootstrap].[Employee] SET [Name]=@Name,[Email]=@Email WHERE [Id]=@id;
                COMMIT;
                SELECT [Id],[Name],[Email] FROM [bootstrap].[Employee] WHERE [Id]=@id;
            END;
            """;
        yield return """
            CREATE PROCEDURE [bootstrap].[ListCompanies]
                @DirectoryId uniqueidentifier, @ObjectId uniqueidentifier, @Offset int, @Limit int
            WITH EXECUTE AS 'kanban_directory'
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @Offset < 0 OR @Limit < 1 OR @Limit > 100 THROW 51003, 'Invalid page.', 1;
                SELECT c.[Id],c.[Name],m.[Id],m.[Administrator],c.[Version]
                FROM [app].[Company] c
                JOIN [app].[Membership] m ON m.[CompanyId]=c.[Id] AND m.[Active]=1
                JOIN [bootstrap].[Employee] u ON u.[Id]=m.[UserId]
                WHERE u.[DirectoryId]=@DirectoryId AND u.[ObjectId]=@ObjectId AND c.[Active]=1
                ORDER BY c.[Id] OFFSET @Offset ROWS FETCH NEXT (@Limit+1) ROWS ONLY;
            END;
            """;
        yield return """
            CREATE PROCEDURE [bootstrap].[FindInvitation] @Hash varchar(64)
            WITH EXECUTE AS 'kanban_directory'
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT [CompanyId] FROM [app].[Invitation] WHERE [TokenHash]=@Hash;
            END;
            """;
        yield return """
            CREATE PROCEDURE [bootstrap].[CreateCompany]
                @DirectoryId uniqueidentifier, @ObjectId uniqueidentifier, @CompanyName nvarchar(200),
                @Key varchar(128), @Hash varchar(64), @Enabled bit, @CorrelationId nvarchar(128)
            WITH EXECUTE AS 'kanban_directory'
            AS
            BEGIN
                SET NOCOUNT ON; SET XACT_ABORT ON;
                BEGIN TRY
                    BEGIN TRANSACTION;
                    DECLARE @user uniqueidentifier = (SELECT [Id] FROM [bootstrap].[Employee]
                        WHERE [DirectoryId]=@DirectoryId AND [ObjectId]=@ObjectId);
                    IF @user IS NULL THROW 51003, 'Identity is unavailable.', 1;
                    DECLARE @lock int, @resource nvarchar(255) = N'company-create:' + CONVERT(nvarchar(36),@user);
                    EXEC @lock=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
                    IF @lock < 0 THROW 51003, 'Company creation is busy.', 1;
                    DECLARE @company uniqueidentifier, @oldHash varchar(64);
                    SELECT @company=[CompanyId],@oldHash=[RequestHash] FROM [bootstrap].[CompanyCreationReceipt]
                        WHERE [UserId]=@user AND [Key]=@Key;
                    IF @company IS NOT NULL
                    BEGIN
                        IF @oldHash <> @Hash THROW 51009, 'Idempotency conflict.', 1;
                        IF NOT EXISTS (SELECT 1 FROM [app].[Membership] WHERE [CompanyId]=@company AND [UserId]=@user AND [Active]=1)
                            THROW 51003, 'Membership is unavailable.', 1;
                    END
                    ELSE
                    BEGIN
                        IF @Enabled=0 THROW 51013, 'Self-service creation is disabled.', 1;
                        IF LEN(LTRIM(RTRIM(@CompanyName)))=0 THROW 51013, 'Name is required.', 1;
                        SET @company=NEWID();
                        DECLARE @member uniqueidentifier=NEWID();
                        INSERT [app].[Company] ([CompanyId],[Id],[Name],[Active]) VALUES (@company,@company,@CompanyName,1);
                        INSERT [app].[Membership] ([CompanyId],[Id],[UserId],[Name],[Email],[Active],[Administrator],[Epoch])
                            SELECT @company,@member,[Id],[Name],[Email],1,1,1 FROM [bootstrap].[Employee] WHERE [Id]=@user;
                        INSERT [bootstrap].[CompanyCreationReceipt] ([UserId],[Key],[RequestHash],[CompanyId])
                            VALUES (@user,@Key,@Hash,@company);
                        EXEC sys.sp_set_session_context @key=N'CompanyId',@value=@company;
                        INSERT [app].[AuditEvent] ([CompanyId],[Id],[ActorId],[ClientId],[TeamId],[ObjectId],[Action],[CorrelationId],[CreatedAt])
                            VALUES (@company,NEWID(),@member,NULL,NULL,@company,N'company.created',@CorrelationId,SYSUTCDATETIME()),
                                   (@company,NEWID(),@member,NULL,NULL,@member,N'company.initial-administrator',@CorrelationId,SYSUTCDATETIME());
                    END;
                    COMMIT;
                    EXEC sys.sp_set_session_context @key=N'CompanyId',@value=NULL;
                    SELECT @company;
                END TRY
                BEGIN CATCH
                    IF XACT_STATE() <> 0 ROLLBACK;
                    EXEC sys.sp_set_session_context @key=N'CompanyId',@value=NULL;
                    THROW;
                END CATCH;
            END;
            """;
    }
}
