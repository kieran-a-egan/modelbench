using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelBench.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RunLeaseAndGradingVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GradingVersion",
                table: "Runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1); // runs that existed before this migration were graded with methodology v1

            migrationBuilder.AddColumn<long>(
                name: "LeaseExpiresMs",
                table: "Runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseOwner",
                table: "Runs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GradingVersion",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresMs",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "Runs");
        }
    }
}
