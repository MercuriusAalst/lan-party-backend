using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Mercurius.LAN.API.Data;

#nullable disable

namespace Mercurius.LAN.API.Migrations;

[DbContext(typeof(MercuriusDBContext))]
[Migration("20261017120000_UserProfilePictures")]
public partial class UserProfilePictures : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProfilePictureUrl",
            schema: "identity",
            table: "users",
            type: "character varying(2048)",
            maxLength: 2048,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProfilePictureUrl",
            schema: "identity",
            table: "users");
    }
}
