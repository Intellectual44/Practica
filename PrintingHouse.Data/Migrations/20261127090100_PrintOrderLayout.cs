using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintingHouse.Data.Migrations
{
    /// <inheritdoc />
    public partial class PrintOrderLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LayoutReference", table: "Orders", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<bool>(
                name: "IsDisabled", table: "Users", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<DateTime>(
                name: "RegisteredAt",
                table: "Customers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "LayoutReference", table: "Orders");
            migrationBuilder.DropColumn(name: "IsDisabled", table: "Users");
            migrationBuilder.DropColumn(
                name: "RegisteredAt",
                table: "Customers");
        }
    }
}
