using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kanban.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProfileErrors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ResolveEmployee(51014));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ResolveEmployee(51003));
        }

        private static string ResolveEmployee(int profileError) => $"""
            CREATE OR ALTER PROCEDURE [bootstrap].[ResolveEmployee]
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
                    IF @Email IS NULL THROW {profileError}, 'A browser profile is required.', 1;
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
    }
}
