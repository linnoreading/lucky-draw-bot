using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using LuckyDrawBot.Models;
using LuckyDrawBot.Tests.Infrastructure;
using Microsoft.Bot.Schema;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;
using static LuckyDrawBot.Services.CompetitionRepositoryService;

namespace LuckyDrawBot.Tests.Features.Competition
{
    public class ViewCompetitionDetailTests : BaseTest
    {
        public ViewCompetitionDetailTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task WhenCompetitionIsCreated_ViewCompetitionDetail_TaskInfoIsGenerated()
        {
            var competition = new OpenCompetitionEntity(Guid.NewGuid())
            {
                MainActivityId = "main activity id",
                Locale = "en-US",
                OffsetHours = 8,
                Gift = "gift name",
                IsCompleted = false,
                Competitors = new List<Competitor> { new Competitor { Name = "user name", AadObjectId = "user aad object id" } },
                WinnerCount = 1,
                WinnerAadObjectIds = new List<string>()
            };

            using (var server = CreateServerFixture(ServerFixtureConfigurations.Default))
            using (var client = server.CreateClient())
            {
                var arrangement = server.Arrange();
                await arrangement.GetOpenCompetitions().InsertOrReplace(competition);

                var response = await client.SendTeamsTaskFetch(new InvokeActionData { UserAction = InvokeActionType.ViewDetail, CompetitionId = competition.Id });

                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var result = await response.Content.ReadAsAsync<TaskModuleTaskInfoResponse>();
                result.Task?.Value?.Card?.Content.Should().NotBeNull();
                var card = ((JObject)result.Task.Value.Card.Content).ToObject<AdaptiveCard>();
                card.Body.Should().HaveCount(1 + competition.Competitors.Count);
            }
        }

    }
}
