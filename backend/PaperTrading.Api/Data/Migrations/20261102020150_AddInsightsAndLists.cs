using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperTrading.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInsightsAndLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Watchlist_Users_UserId",
                table: "Watchlist");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Watchlist",
                table: "Watchlist");

            migrationBuilder.RenameTable(
                name: "Watchlist",
                newName: "WatchlistItems");

            migrationBuilder.RenameColumn(
                name: "UserId",
                table: "WatchlistItems",
                newName: "WatchListId");

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimId",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_WatchlistItems",
                table: "WatchlistItems",
                columns: new[] { "WatchListId", "Symbol" });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    YouTubeChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    BackfillCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPolledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPollError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyBriefs",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyBriefs", x => x.Date);
                });

            migrationBuilder.CreateTable(
                name: "PriceBars",
                columns: table => new
                {
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    High = table.Column<decimal>(type: "TEXT", nullable: false),
                    Low = table.Column<decimal>(type: "TEXT", nullable: false),
                    Close = table.Column<decimal>(type: "TEXT", nullable: false),
                    ClosesAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceBars", x => new { x.Symbol, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "WatchLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchLists_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Every existing account gets its main list, with the same Id as the user. The items' UserId column
            // becomes WatchListId above, so each person's saved tickers land in their main list unchanged.
            migrationBuilder.Sql("""
                INSERT INTO "WatchLists" ("Id", "UserId", "Name", "CreatedAt")
                SELECT "Id", "Id", 'Favorites', "CreatedAt" FROM "Users";
                """);

            migrationBuilder.CreateTable(
                name: "ChannelFollows",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FollowedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelFollows", x => new { x.UserId, x.ChannelId });
                    table.ForeignKey(
                        name: "FK_ChannelFollows_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChannelFollows_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Videos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    YouTubeVideoId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    DiscoveredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExtractionModel = table.Column<string>(type: "TEXT", nullable: true),
                    PromptVersion = table.Column<string>(type: "TEXT", nullable: true),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    RejectedClaims = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Videos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Videos_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Claims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VideoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    TargetPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    HorizonDays = table.Column<int>(type: "INTEGER", nullable: false),
                    HorizonStated = table.Column<bool>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<string>(type: "TEXT", nullable: false),
                    Quote = table.Column<string>(type: "TEXT", nullable: false),
                    TimestampSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Reasoning = table.Column<string>(type: "TEXT", nullable: true),
                    Speaker = table.Column<string>(type: "TEXT", nullable: true),
                    QuoteVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    MadeAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvesAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    ExitPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    BenchmarkEntryPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    BenchmarkExitPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    ReturnPercent = table.Column<decimal>(type: "TEXT", nullable: true),
                    ExcessReturnPercent = table.Column<decimal>(type: "TEXT", nullable: true),
                    TargetHit = table.Column<bool>(type: "INTEGER", nullable: true),
                    ScoredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Review = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Claims_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Claims_Videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "Videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Transcripts",
                columns: table => new
                {
                    VideoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transcripts", x => x.VideoId);
                    table.ForeignKey(
                        name: "FK_Transcripts_Videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "Videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelFollows_ChannelId",
                table: "ChannelFollows",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_YouTubeChannelId",
                table: "Channels",
                column: "YouTubeChannelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Claims_ChannelId_MadeAt",
                table: "Claims",
                columns: new[] { "ChannelId", "MadeAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_Status",
                table: "Claims",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Claims_Ticker_MadeAt",
                table: "Claims",
                columns: new[] { "Ticker", "MadeAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_VideoId",
                table: "Claims",
                column: "VideoId");

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_FetchedAt",
                table: "Transcripts",
                column: "FetchedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Videos_ChannelId",
                table: "Videos",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Videos_Status_NextAttemptAt",
                table: "Videos",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Videos_YouTubeVideoId",
                table: "Videos",
                column: "YouTubeVideoId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchLists_UserId_Name",
                table: "WatchLists",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WatchlistItems_WatchLists_WatchListId",
                table: "WatchlistItems",
                column: "WatchListId",
                principalTable: "WatchLists",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WatchlistItems_WatchLists_WatchListId",
                table: "WatchlistItems");

            migrationBuilder.DropTable(
                name: "ChannelFollows");

            migrationBuilder.DropTable(
                name: "Claims");

            migrationBuilder.DropTable(
                name: "DailyBriefs");

            migrationBuilder.DropTable(
                name: "PriceBars");

            migrationBuilder.DropTable(
                name: "Transcripts");

            migrationBuilder.DropTable(
                name: "WatchLists");

            migrationBuilder.DropTable(
                name: "Videos");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropPrimaryKey(
                name: "PK_WatchlistItems",
                table: "WatchlistItems");

            migrationBuilder.DropColumn(
                name: "ClaimId",
                table: "Orders");

            migrationBuilder.RenameTable(
                name: "WatchlistItems",
                newName: "Watchlist");

            migrationBuilder.RenameColumn(
                name: "WatchListId",
                table: "Watchlist",
                newName: "UserId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Watchlist",
                table: "Watchlist",
                columns: new[] { "UserId", "Symbol" });

            migrationBuilder.AddForeignKey(
                name: "FK_Watchlist_Users_UserId",
                table: "Watchlist",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
