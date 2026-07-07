using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ashes.Registry.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A standalone SQLite FTS5 index over package namespace/description/keywords, kept in sync
            // with the Packages table by triggers so every write path (including direct seeding) stays
            // consistent without app code. REGISTRY_API 5.2 / 7.
            migrationBuilder.Sql("CREATE VIRTUAL TABLE PackageSearch USING fts5(namespace, description, keywords);");

            migrationBuilder.Sql(
                "CREATE TRIGGER Packages_search_ai AFTER INSERT ON Packages BEGIN " +
                "INSERT INTO PackageSearch(namespace, description, keywords) " +
                "VALUES (new.Namespace, new.Description, new.KeywordsJson); END;");

            migrationBuilder.Sql(
                "CREATE TRIGGER Packages_search_ad AFTER DELETE ON Packages BEGIN " +
                "DELETE FROM PackageSearch WHERE namespace = old.Namespace; END;");

            migrationBuilder.Sql(
                "CREATE TRIGGER Packages_search_au AFTER UPDATE ON Packages BEGIN " +
                "DELETE FROM PackageSearch WHERE namespace = old.Namespace; " +
                "INSERT INTO PackageSearch(namespace, description, keywords) " +
                "VALUES (new.Namespace, new.Description, new.KeywordsJson); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Packages_search_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Packages_search_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Packages_search_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS PackageSearch;");
        }
    }
}
