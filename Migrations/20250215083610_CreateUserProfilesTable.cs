using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class CreateUserProfilesTable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'UserProfiles') THEN
                        CREATE TABLE ""UserProfiles"" (
                            ""Id"" SERIAL PRIMARY KEY,
                            ""FirstName"" VARCHAR(50) NOT NULL,
                            ""LastName"" VARCHAR(50) NOT NULL,
                            ""PhotoUrl"" VARCHAR(255),
                            ""UserId"" INT NOT NULL,
                            CONSTRAINT ""FK_UserProfiles_Users_UserId"" FOREIGN KEY (""UserId"") REFERENCES ""Users"" (""Id"") ON DELETE CASCADE
                        );
                        CREATE UNIQUE INDEX ""IX_UserProfiles_UserId"" ON ""UserProfiles"" (""UserId"");
                    END IF;
                END
                $$;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserProfiles");
        }
    }
}
