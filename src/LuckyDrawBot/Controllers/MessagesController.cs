using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using LuckyDrawBot.Models;
using LuckyDrawBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LuckyDrawBot.Controllers
{
    [ApiController]
    [Produces("application/json")]
    public class MessagesController : ControllerBase
    {
        private class CreateCompetitionParameters
        {
            public string Gift { get; set; }
            public string GiftImageUrl { get; set; }
            public int WinnerCount { get; set; }
            public DateTimeOffset PlannedDrawTime { get; set; }
            public double OffsetHours { get; set; }
        }

        private const char ChineseCommaCharacter = '，';
        private readonly ILogger<MessagesController> _logger;
        private readonly IBotClientFactory _botClientFactory;
        private readonly ICompetitionService _competitionService;
        private readonly IActivityBuilder _activityBuilder;
        private readonly ITimerService _timerService;
        private readonly IDateTimeService _dateTimeService;
        private readonly ILocalizationFactory _localizationFactory;

        public MessagesController(
            ILogger<MessagesController> logger,
            IBotClientFactory botClientFactory,
            ICompetitionService competitionService,
            IActivityBuilder activityBuilder,
            ITimerService timerService,
            IDateTimeService dateTimeService,
            ILocalizationFactory localizationFactory)
        {
            _logger = logger;
            _botClientFactory = botClientFactory;
            _competitionService = competitionService;
            _activityBuilder = activityBuilder;
            _timerService = timerService;
            _dateTimeService = dateTimeService;
            _localizationFactory = localizationFactory;
        }

        [HttpPost]
        [Route("messages")]
        [ProducesResponseType((int)HttpStatusCode.OK)]
        public async Task<IActionResult> GetMessage([FromBody]Activity activity)
        {
            _logger.LogInformation($"ChannelId:{activity.ChannelId} Type:{activity.Type} Action:{activity.Action} ValueType:{activity.ValueType} Value:{activity.Value}");

            if (activity.ChannelId != "msteams")
            {
                return Ok();
            }

            if (activity.Type == ActivityTypes.Invoke)
            {
                InvokeActionData invokeActionData;
                if (activity.Name == "task/fetch")
                {
                    var data = ((JObject)activity.Value).GetValue("data");
                    invokeActionData = JsonConvert.DeserializeObject<InvokeActionData>(JsonConvert.SerializeObject(data));
                }
                else
                {
                    invokeActionData = JsonConvert.DeserializeObject<InvokeActionData>(JsonConvert.SerializeObject(activity.Value));
                }

                switch(invokeActionData.UserAction)
                {
                    case InvokeActionType.Join:
                        await HandleJoinCompetitionAction(invokeActionData, activity);
                        return Ok();
                    case InvokeActionType.ViewDetail:
                        var competitionDetailResponse = await HandleViewCompetitionDetailAction(invokeActionData, activity);
                        return Ok(competitionDetailResponse);
                    default:
                        throw new Exception("Unknown invoke action type: " + invokeActionData.UserAction);
                }
            }
            else if (activity.Type == ActivityTypes.Message)
            {
                var succeeded = await HandleCompetitionInitialization(activity);
                if (!succeeded)
                {
                    await HandleDisplayHelp(activity);
                    return Ok();
                }
            }
            return Ok();
        }

        private async Task<bool> HandleCompetitionInitialization(Activity activity)
        {
            var parameters = ParseCreateCompetitionParameters(activity);
            if (parameters == null)
            {
                return false;
            }

            var channelData = activity.GetChannelData<TeamsChannelData>();
            var competition = await _competitionService.Create(
                                                               activity.ServiceUrl,
                                                               Guid.Parse(channelData.Tenant.Id),
                                                               channelData.Team.Id,
                                                               channelData.Channel.Id,
                                                               parameters.PlannedDrawTime,
                                                               activity.Locale,
                                                               parameters.OffsetHours,
                                                               parameters.Gift,
                                                               parameters.GiftImageUrl,
                                                               parameters.WinnerCount,
                                                               activity.From.Name,
                                                               activity.From.AadObjectId);

            var mainActivity = _activityBuilder.CreateMainActivity(competition);
            using (var botClient = _botClientFactory.CreateBotClient(activity.ServiceUrl))
            {
                var mainMessage = await botClient.SendToConversationAsync(mainActivity);
                await _competitionService.UpdateMainActivity(competition.Id, mainMessage.Id);
            }

            await _timerService.AddScheduledHttpRequest(
                competition.PlannedDrawTime,
                "POST",
                Url.Action(nameof(CompetitionsController.DrawForCompetition), "Competitions", new { competitionId = competition.Id }));

            return true;
        }

        private async Task HandleJoinCompetitionAction(InvokeActionData invokeActionData, Activity activity)
        {
            var competition = await _competitionService.AddCompetitor(invokeActionData.CompetitionId, activity.From.AadObjectId, activity.From.Name);
            var updatedActivity = _activityBuilder.CreateMainActivity(competition);
            using (var botClient = _botClientFactory.CreateBotClient(activity.ServiceUrl))
            {
                await botClient.UpdateActivityAsync(competition.ChannelId, competition.MainActivityId, updatedActivity);
            }
        }

        private async Task<TaskModuleTaskInfoResponse> HandleViewCompetitionDetailAction(InvokeActionData invokeActionData, Activity activity)
        {
            var competition = await _competitionService.GetCompetition(invokeActionData.CompetitionId);
            var taskInfoResponse = _activityBuilder.CreateCompetitionDetailTaskInfoResponse(competition);
            return taskInfoResponse;
        }

        private async Task HandleDisplayHelp(Activity activity)
        {
            var localization = _localizationFactory.Create(activity.Locale);
            var help = activity.CreateReply(localization["Help.Message"]);
            using (var botClient = _botClientFactory.CreateBotClient(activity.ServiceUrl))
            {
                await botClient.SendToConversationAsync(help);
            }
        }

        private CreateCompetitionParameters ParseCreateCompetitionParameters(Activity activity)
        {
            const string MentionBotEndingFlag = "</at>";
            var text = activity.Text;
            if (text.IndexOf(MentionBotEndingFlag) < 0)
            {
                return null;
            }
            text = text.Substring(text.IndexOf(MentionBotEndingFlag) + MentionBotEndingFlag.Length);

            var parts = text.Split(',', ChineseCommaCharacter).Select(p => p.Trim()).ToArray();
            if (parts.Length < 2)
            {
                return null;
            }

            var gift = parts[0].Trim();
            int winnerCount;
            if (!int.TryParse(parts[1], out winnerCount))
            {
                return null;
            }
            var offset = activity.LocalTimestamp.HasValue ? activity.LocalTimestamp.Value.Offset : TimeSpan.Zero;
            DateTimeOffset plannedDrawTime;
            if (parts.Length > 2)
            {
                var timeString = parts[2].Trim();
                if (TryParseTimeDuration(timeString, out TimeSpan duration))
                {
                    plannedDrawTime = _dateTimeService.UtcNow.Add(duration);
                }
                else
                {
                    DateTimeOffset time;
                    if (!DateTimeOffset.TryParse(timeString, CultureInfo.GetCultureInfo(activity.Locale), DateTimeStyles.None, out time))
                    {
                        return null;
                    }
                    plannedDrawTime = new DateTimeOffset(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second, 0, offset).ToUniversalTime();
                }
            }
            else
            {
                plannedDrawTime = _dateTimeService.UtcNow.AddMinutes(1);
            }
            var giftImageUrl = parts.Length > 3 ? parts[3].Trim() : string.Empty;

            var localization = _localizationFactory.Create(activity.Locale);
            return new CreateCompetitionParameters
            {
                Gift = gift,
                GiftImageUrl = giftImageUrl,
                WinnerCount = winnerCount,
                PlannedDrawTime = plannedDrawTime,
                OffsetHours = offset.TotalHours
            };
        }

        // We will leverage LUIS to parse the input time
        private bool TryParseTimeDuration(string time, out TimeSpan duration)
        {
            var minutePostfixes = new string[] { "m", "min", "mins", "minute", "minutes", "分钟" };
            var hourPostfixes = new string[] { "h", "hr", "hrs", "hour", "hours", "小时" };

            foreach (var minutePostfix in minutePostfixes)
            {
                if (time.EndsWith(minutePostfix, StringComparison.InvariantCultureIgnoreCase))
                {
                    if (double.TryParse(time.Substring(0, time.Length - minutePostfix.Length), out double minutes))
                    {
                        duration = TimeSpan.FromMinutes(minutes);
                        return true;
                    }
                }
            }
            foreach (var hourPostfix in hourPostfixes)
            {
                if (time.EndsWith(hourPostfix, StringComparison.InvariantCultureIgnoreCase))
                {
                    if (double.TryParse(time.Substring(0, time.Length - hourPostfix.Length), out double hours))
                    {
                        duration = TimeSpan.FromHours(hours);
                        return true;
                    }
                }
            }
            duration = TimeSpan.Zero;
            return false;
        }
    }
}
