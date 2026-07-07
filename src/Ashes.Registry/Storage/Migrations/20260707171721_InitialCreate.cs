using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ashes.Registry.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Packages",
                columns: table => new
                {
                    Namespace = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    KeywordsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Downloads = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packages", x => x.Namespace);
                });

            migrationBuilder.CreateTable(
                name: "Tokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    HashedSecret = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Owners",
                columns: table => new
                {
                    PackageNamespace = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Owners", x => new { x.PackageNamespace, x.AccountId });
                    table.ForeignKey(
                        name: "FK_Owners_Packages_PackageNamespace",
                        column: x => x.PackageNamespace,
                        principalTable: "Packages",
                        principalColumn: "Namespace",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Versions",
                columns: table => new
                {
                    Namespace = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    DependenciesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Yanked = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Versions", x => new { x.Namespace, x.Version });
                    table.ForeignKey(
                        name: "FK_Versions_Packages_Namespace",
                        column: x => x.Namespace,
                        principalTable: "Packages",
                        principalColumn: "Namespace",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Name",
                table: "Accounts",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tokens_HashedSecret",
                table: "Tokens",
                column: "HashedSecret",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "Owners");

            migrationBuilder.DropTable(
                name: "Tokens");

            migrationBuilder.DropTable(
                name: "Versions");

            migrationBuilder.DropTable(
                name: "Packages");
        }
    }
}
