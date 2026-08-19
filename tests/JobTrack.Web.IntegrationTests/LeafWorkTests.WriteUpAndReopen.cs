namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Domain.Schedules;
using Microsoft.Data.Sqlite;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;

public sealed partial class LeafWorkTests
{
	[Fact]
	public async Task A_prior_participant_can_reopen_and_start_for_themselves_from_the_work_page()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.reopen-participant");
		var newOwnerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.reopen-new-owner");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Terminal leaf");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = session.Id,
			Version = session.Version,
		});
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			NewAchievement = Achievement.Unsuccessful,
			Reason = "Did not work out",
			Version = 2,
		});
		_ = await seedClient.Jobs.EditAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			NodeId = leaf.Id,
			Description = leaf.Description,
			OwnerUserId = newOwnerId,
			Priority = Priority.Medium,
			Version = leaf.Version,
		});
		var authCookie = await client.SignInAsync("work.reopen-participant");
		var pageResponse = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var pageBody = await pageResponse.Content.ReadAsStringAsync();
		pageBody.Should().Contain(">Reopen and start session</button>");
		pageBody.Should().NotContain(">Reopen without starting</button>");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostReopenAndStartAsync(authCookie, cookie, token, leaf.Id, 3, "Work resumed", workerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Job reopened. Session started.");
	}

	/// <summary>
	///     ADR 0051 end to end, on the "build a house" shape that reported it: Excavate foundations is
	///     closed as Success, Pour foundations requires it and has a session running, and reopening
	///     Excavate used to fail with "Someone else changed this leaf since the page was loaded" -- a
	///     concurrency message for a state nothing had concurrently changed, which no reload could clear.
	///     The reopen now succeeds; Pour foundations keeps its running session but shows as blocked and
	///     is refused if it tries to close.
	/// </summary>
	[Fact]
	public async Task Reopening_a_prerequisite_with_a_live_dependent_succeeds_and_blocks_the_dependent_from_completing()
	{
		var (workerId, required, dependent) = await SeedSuccessfulPrerequisiteWithLiveDependentAsync("work.reopen-dependent");
		var authCookie = await client.SignInAsync("work.reopen-dependent");

		var (reopenCookie, reopenToken) = await GetWorkFormAsync(authCookie, required.Id, workerId);
		var reopenResponse = await PostReopenAndStartAsync(
			authCookie, reopenCookie, reopenToken, required.Id, 3, "Closed by mistake", workerId);

		reopenResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reopenedBody = await (await client.FollowRedirectAsync(reopenResponse, authCookie)).Content.ReadAsStringAsync();
		reopenedBody.Should().Contain("Job reopened. Session started.");
		reopenedBody.Should().NotContain("Someone else changed this leaf");

		var dependentBody = await (await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={dependent.Id.Value}", authCookie)).Content.ReadAsStringAsync();
		dependentBody.Should().Contain("status-pill-blocked", "a dependent whose prerequisite was reopened is blocked, whatever its achievement");
		dependentBody.Should().NotContain(">Complete job</button>", "a blocked leaf cannot be closed, so the page must not offer it");

		var dependentSessions = await GetSessionsAsync(dependent.Id);
		var (completeCookie, completeToken) = await GetWorkFormAsync(authCookie, dependent.Id, workerId);
		var completeResponse = await PostCompleteAsync(
			authCookie, completeCookie, completeToken, dependent.Id, 2,
			[.. dependentSessions.Select(s => (s.Id.Value, s.Version))]);

		completeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var refusedBody = await (await client.FollowRedirectAsync(completeResponse, authCookie)).Content.ReadAsStringAsync();
		refusedBody.Should().Contain("prerequisite");
		var dependentWork = await seedClient.Query.GetLeafWorkPageAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = dependent.Id,
		});
		dependentWork.Achievement.Should().Be(Achievement.InProgress, "the refused completion must leave the dependent exactly as it was");
		dependentWork.ActiveSessions.Should().ContainSingle("the worker's running session is never ended behind their back");
	}

	/// <summary>
	///     The other route to the same ADR 0051 state, and the other half of the UI's honesty about it:
	///     the prerequisite is reopened through the "Change outcome" dropdown (<c>SetAchievementAsync</c>,
	///     which never carried the rejected dependent-work check), and the blocked dependent's own
	///     dropdown then offers no terminal option at all rather than a Save the command would refuse.
	/// </summary>
	[Fact]
	public async Task A_blocked_dependent_offers_no_terminal_outcome_option()
	{
		var (workerId, required, dependent) = await SeedSuccessfulPrerequisiteWithLiveDependentAsync("work.blocked-outcome");
		var administratorContext = new CommandContext {
			Actor = administratorId,
			CorrelationId = Guid.NewGuid(),
		};
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = administratorContext,
			JobNodeId = required.Id,
			NewAchievement = Achievement.Waiting,
			Reason = "Closed by mistake",
			Version = 3,
		});
		var authCookie = await client.SignInAsync("work.blocked-outcome");

		var body = await (await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={dependent.Id.Value}", authCookie)).Content.ReadAsStringAsync();

		body.Should().Contain("Blocked by a prerequisite.");
		body.Should().NotContain("value=\"Success\"", "no route on the page may offer to close a blocked job");
		body.Should().NotContain("value=\"Cancelled\"");
		body.Should().NotContain("value=\"Unsuccessful\"");
		_ = workerId;
	}

	/// <summary>
	///     ADR 0051 for a dependent that had already finished: Pour foundations is closed as Success
	///     when Excavate foundations is reopened underneath it, so it is now blocked. Reopening it too is
	///     the usual next step of the same correction and stays available -- but only without starting a
	///     session, since starting work on a blocked leaf is barred (spec §6). The page must offer the
	///     route that works and withhold the one that cannot, and must never answer a posted
	///     reopen-and-start with an unhandled exception.
	/// </summary>
	[Fact]
	public async Task A_closed_dependent_can_still_be_reopened_after_its_prerequisite_is_reopened()
	{
		var (workerId, required, dependent) = await SeedSuccessfulPrerequisiteWithLiveDependentAsync("work.closed-dependent");
		var dependentSessions = await GetSessionsAsync(dependent.Id);
		_ = await seedClient.Work.CompleteLeafAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = dependent.Id,
			Version = 2,
			ExpectedActiveSessions = [
				.. dependentSessions.Select(s => new ExpectedActiveSession {
					Id = s.Id, Version = s.Version,
				}),
			],
		});
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = required.Id,
			NewAchievement = Achievement.Waiting,
			Reason = "Closed by mistake",
			Version = 3,
		});
		var authCookie = await client.SignInAsync("work.closed-dependent");

		var body = await (await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={dependent.Id.Value}", authCookie)).Content.ReadAsStringAsync();

		body.Should().NotContain(">Reopen and start session</button>", "starting a session on a blocked leaf is barred");
		body.Should().Contain("Blocked by a prerequisite.", "the page must say why the usual reopen route is unavailable");
		body.Should().Contain("reopened without starting work", "and must point at the route that does work");

		// Posting it anyway (a stale page, or a hand-rolled request) is refused with a message, not a 500.
		var (cookie, token) = await GetWorkFormAsync(authCookie, dependent.Id, workerId);
		var response = await PostReopenAndStartAsync(authCookie, cookie, token, dependent.Id, 3, "Trying anyway", workerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var refusedBody = await (await client.FollowRedirectAsync(response, authCookie)).Content.ReadAsStringAsync();
		refusedBody.Should().Contain("prerequisite");

		// The route that does work: reopen without starting, via Change outcome.
		var reopened = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = dependent.Id,
			NewAchievement = Achievement.Waiting,
			Reason = "Reopening the dependent too",
			Version = 3,
		});
		reopened.Achievement.Should().Be(Achievement.Waiting);
	}

	/// <summary>
	///     The "build a house" pair the bug report used: Excavate foundations closed as
	///     <see cref="Achievement.Success" /> at leaf-work version 3, and Pour foundations requiring it
	///     with a session running right now at leaf-work version 2.
	/// </summary>
	private async Task<(AppUserId WorkerId, JobNodeResult Required, JobNodeResult Dependent)>
		SeedSuccessfulPrerequisiteWithLiveDependentAsync(string userName)
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, userName);
		var required = await AddWorkedLeafAsync(rootId, workerId, "Excavate foundations");
		var dependent = await AddWorkedLeafAsync(rootId, workerId, "Pour foundations");
		await seedClient.Jobs.AddPrerequisiteAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});
		var requiredSession = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = required.Id,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = requiredSession.Id,
			Version = requiredSession.Version,
		});
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = required.Id,
			NewAchievement = Achievement.Success,
			Reason = "Foundations dug",
			Version = 2,
		});
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = dependent.Id,
			WorkedByUserId = workerId,
		});

		return (workerId, required, dependent);
	}

	[Fact]
	public async Task Reopening_and_starting_for_yourself_does_not_pin_the_sessions_filter_to_yourself()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.reopen-filter");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Terminal leaf for filter check");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = session.Id,
			Version = session.Version,
		});
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			NewAchievement = Achievement.Unsuccessful,
			Reason = "Did not work out",
			Version = 2,
		});
		var authCookie = await client.SignInAsync("work.reopen-filter");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostReopenAndStartAsync(authCookie, cookie, token, leaf.Id, 2, "Work resumed", workerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		// The reopened-for worker (here, the actor themselves) must never leak into the Sessions
		// filter -- the "workedByUserId" name collision this regresses used to carry the actor's own
		// id onto the redirect as an explicit filter value.
		response.Headers.Location!.OriginalString.Should().NotContain("orkedByUserId");
		var body = await (await client.FollowRedirectAsync(response, authCookie)).Content.ReadAsStringAsync();
		body.Should().NotContain("Sessions worked by", "the unfiltered Everyone view must survive reopening for yourself");
	}

	[Fact]
	public async Task Reopening_and_starting_a_session_saves_the_write_up_typed_beside_it()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.reopen-writeup");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Cancelled leaf with write-up");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = session.Id,
			Version = session.Version,
		});
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			NewAchievement = Achievement.Cancelled,
			Reason = "Client changed their mind",
			Version = 2,
		});
		var authCookie = await client.SignInAsync("work.reopen-writeup");

		var (writeUpCookie, writeUpToken) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var writeUpResponse = await PostSaveWriteUpAsync(
			authCookie, writeUpCookie, writeUpToken, leaf.Id, leaf.Version, "Client reinstated the original scope.");
		writeUpResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostReopenAndStartAsync(
			authCookie, cookie, token, leaf.Id, 2, "Client changed their mind again", workerId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var current = await seedClient.Query.GetJobNodeAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				NodeId = leaf.Id,
			});
		current.Node.WriteUp.Should().Be("Client reinstated the original scope.");
	}

	[Fact]
	public async Task Changing_the_outcome_saves_the_write_up_typed_beside_it()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.outcome-writeup");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Leaf for outcome write-up");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = session.Id,
			Version = session.Version,
		});
		var authCookie = await client.SignInAsync("work.outcome-writeup");

		var (writeUpCookie, writeUpToken) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var writeUpResponse = await PostSaveWriteUpAsync(
			authCookie, writeUpCookie, writeUpToken, leaf.Id, leaf.Version, "Superseded; see the replacement job for details.");
		writeUpResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=SetAchievement");
		request.Headers.Add("Cookie", $"{authCookie}; {cookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = leaf.Id.Value.ToString(CultureInfo.InvariantCulture),
			["leafWorkVersion"] = "2",
			["newAchievement"] = nameof(Achievement.Cancelled),
			["reason"] = "Superseded by another job",
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var current = await seedClient.Query.GetJobNodeAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				NodeId = leaf.Id,
			});
		current.Node.WriteUp.Should().Be("Superseded; see the replacement job for details.");
	}

	/// <summary>
	///     The heading names this leaf; the back affordance goes wherever the page was reached from.
	///     Making the title itself the link conflated the two — it read as "open node N" while landing
	///     on a different node's Browse — so the title is plain text and "Back" carries the return.
	/// </summary>
	[Fact]
	public async Task The_work_page_title_is_plain_text_beside_a_back_link_to_where_it_was_reached_from()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.back-link");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Back link leaf");
		var authCookie = await client.SignInAsync("work.back-link");
		var returnUrl = $"/Jobs/Browse?nodeId={rootId.Value}&unassignedOnly=False";

		var response = await client.GetAuthenticatedAsync(
			$"/Jobs/Work?leafNodeId={leaf.Id.Value}&returnUrl={Uri.EscapeDataString(returnUrl)}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		var head = body[body.IndexOf("jt-page-head", StringComparison.Ordinal)..];
		head = head[..head.IndexOf("</div>", StringComparison.Ordinal)];
		head.Should().NotContain(
			$"Back link leaf (ID {leaf.Id.Value})</a>",
			"the title names this leaf, so it must not be a link to another node's page");
		head.Should().Contain($"href=\"/Jobs/Browse?nodeId={rootId.Value}&amp;unassignedOnly=False\"");
		head.Should().Contain(">Back</a>");
	}

	[Fact]
	public async Task The_work_page_back_link_falls_back_to_browse_rooted_at_this_leaf()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.back-fallback");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Back fallback leaf");
		var authCookie = await client.SignInAsync("work.back-fallback");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		var head = body[body.IndexOf("jt-page-head", StringComparison.Ordinal)..];
		head = head[..head.IndexOf("</div>", StringComparison.Ordinal)];
		head.Should().Contain($"href=\"/Jobs/Browse?nodeId={leaf.Id.Value}\"");
		head.Should().Contain(">Back</a>");
	}

	[Fact]
	public async Task The_work_page_shows_the_leafs_current_write_up_in_a_prominent_multi_line_field()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.writeup-shown");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Write-up leaf");
		_ = await seedClient.Jobs.EditAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			NodeId = leaf.Id,
			Description = leaf.Description,
			WriteUp = "Existing notes from a prior worker.",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
			Version = leaf.Version,
		});
		var authCookie = await client.SignInAsync("work.writeup-shown");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("id=\"writeup\"");
		body.Should().Contain("<textarea id=\"writeUp\"");
		body.Should().Contain("rows=\"6\"", "the write-up field is multi-line, not a single-line input");
		body.Should().Contain("Existing notes from a prior worker.");
	}

	[Fact]
	public async Task The_ending_section_carries_pause_complete_and_save_write_up_in_one_form()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.ending-one-form");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "One ending form");
		_ = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await client.SignInAsync("work.ending-one-form");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		var section = body[body.IndexOf("id=\"end-session\"", StringComparison.Ordinal)..];
		section = section[..section.IndexOf("</form>", StringComparison.Ordinal)];
		section.Should().Contain("<textarea id=\"writeUp\"", "the write-up posts with whichever ending button is pressed");
		section.Should().Contain("Pause job");
		section.Should().Contain("Complete job");
		section.Should().Contain("Save write-up");
	}

	/// <summary>
	///     A paused leaf (<c>InProgress</c>, nobody clocked on) is a valid, expected state — ADR 0045
	///     allows zero active sessions from <c>InProgress</c>, and Pause job produces exactly this. The
	///     page names it rather than looking identical to a leaf nobody has started, and still offers
	///     the ending decision, since completing from zero sessions is the supported path.
	/// </summary>
	[Fact]
	public async Task A_paused_leaf_reads_as_paused_and_can_still_be_completed_with_its_write_up()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.paused");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Paused leaf");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = session.Id,
			Version = session.Version,
		});
		var authCookie = await client.SignInAsync("work.paused");

		var response = await client.GetAuthenticatedAsync($"/Jobs/Work?leafNodeId={leaf.Id.Value}", authCookie);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("status-pill-paused", "the paused state is named, not left to look like a leaf nobody started");
		body.Should().NotContain("Finish 0 sessions and complete job", "a paused leaf has no sessions left to finish");
		var section = body[body.IndexOf("id=\"end-session\"", StringComparison.Ordinal)..];
		section = section[..section.IndexOf("</form>", StringComparison.Ordinal)];
		section.Should().Contain("<textarea id=\"writeUp\"", "the paused leaf's completion carries its write-up too");
		section.Should().Contain("Complete job");
		section.Should().Contain("Save write-up");
		section.Should().NotContain("Pause job", "there is no session left to pause");
	}

	[Fact]
	public async Task A_paused_leaf_can_be_completed_with_no_remaining_sessions()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.paused-complete");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Paused then completed");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = session.Id,
			Version = session.Version,
		});
		var authCookie = await client.SignInAsync("work.paused-complete");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(
			authCookie, cookie, token, leaf.Id, 2, [], nodeVersion: leaf.Version, writeUp: "Wrapped up after the pause.");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Job completed.");
		var current = await seedClient.Query.GetJobNodeAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				NodeId = leaf.Id,
			});
		current.Node.WriteUp.Should().Be("Wrapped up after the pause.");
	}

	[Fact]
	public async Task Completing_a_job_saves_the_write_up_typed_beside_it()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.complete-writeup");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Complete with write-up");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await client.SignInAsync("work.complete-writeup");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostCompleteAsync(
			authCookie, cookie, token, leaf.Id, 2, [(session.Id.Value, session.Version)],
			nodeVersion: leaf.Version, writeUp: "Ran long, but the fit is sound.");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var current = await seedClient.Query.GetJobNodeAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				NodeId = leaf.Id,
			});
		current.Node.WriteUp.Should().Be("Ran long, but the fit is sound.");
		var leafWork = await seedClient.Query.GetLeafWorkAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				JobNodeId = leaf.Id,
			}, CancellationToken.None);
		leafWork.Achievement.Should().Be(Achievement.Success);
	}

	[Fact]
	public async Task Pausing_a_session_saves_the_write_up_typed_beside_it()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.pause-writeup");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Pause with write-up");
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
			WorkedByUserId = workerId,
		});
		var authCookie = await client.SignInAsync("work.pause-writeup");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		var response = await PostFinishAsync(
			authCookie, cookie, token, leaf.Id, workerId, session.Id.Value, session.Version,
			nodeVersion: leaf.Version, writeUp: "Stopping here; awaiting the replacement part.");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var current = await seedClient.Query.GetJobNodeAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				NodeId = leaf.Id,
			});
		current.Node.WriteUp.Should().Be("Stopping here; awaiting the replacement part.");
		(await GetSessionsAsync(leaf.Id)).Should().ContainSingle().Which.FinishedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task A_controlling_worker_can_save_the_leafs_write_up_without_affecting_other_fields()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "work.writeup-save");
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Write-up save leaf");
		var authCookie = await client.SignInAsync("work.writeup-save");

		var (cookie, token) = await GetWorkFormAsync(authCookie, leaf.Id, workerId);
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=SaveWriteUp");
		request.Headers.Add("Cookie", $"{authCookie}; {cookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = leaf.Id.Value.ToString(CultureInfo.InvariantCulture),
			["nodeVersion"] = leaf.Version.ToString(CultureInfo.InvariantCulture),
			["writeUp"] = "Finished the trim work; used oak instead of pine per client request.",
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Write-up saved.");
		var current = await seedClient.Query.GetJobNodeAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				NodeId = leaf.Id,
			});
		current.Node.WriteUp.Should().Be("Finished the trim work; used oak instead of pine per client request.");
		current.Node.Description.Should().Be(leaf.Description);
		current.Node.OwnerUserId.Should().Be(workerId);
	}

	private async Task<HttpResponseMessage> PostCompleteAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId, long leafWorkVersion,
		IReadOnlyList<(long SessionId, long Version)> sessions, string? finishedAt = null, string? completionNote = null,
		Achievement? finalAchievement = null, long? nodeVersion = null, string? writeUp = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=Complete");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var pairs = new List<KeyValuePair<string, string>> {
			new("LeafNodeId", leafNodeId.Value.ToString(CultureInfo.InvariantCulture)), new("leafWorkVersion", leafWorkVersion.ToString(CultureInfo.InvariantCulture)), new("__RequestVerificationToken", token),
		};
		foreach (var (sessionId, version) in sessions) {
			pairs.Add(new("endSessionId", sessionId.ToString(CultureInfo.InvariantCulture)));
			pairs.Add(new("endSessionVersion", version.ToString(CultureInfo.InvariantCulture)));
		}

		if (finishedAt is not null) {
			pairs.Add(new("completionFinishedAt", finishedAt));
		}

		if (completionNote is not null) {
			pairs.Add(new("completionNote", completionNote));
		}

		if (finalAchievement is Achievement achievement) {
			pairs.Add(new("finalAchievement", achievement.ToString()));
		}

		if (nodeVersion is long nodeVersionValue) {
			pairs.Add(new("nodeVersion", nodeVersionValue.ToString(CultureInfo.InvariantCulture)));
		}

		if (writeUp is not null) {
			pairs.Add(new("writeUp", writeUp));
		}

		request.Content = new FormUrlEncodedContent(pairs);

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostReopenAndStartAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId, long leafWorkVersion, string reason,
		AppUserId workedByUserId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=ReopenAndStart");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["leafWorkVersion"] = leafWorkVersion.ToString(CultureInfo.InvariantCulture),
			["reason"] = reason,
			["reopenWorkedByUserId"] = workedByUserId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	/// <summary>Posts the leaf-level "Pause job" button with the confirmed active-session set the page rendered.</summary>
	private async Task<HttpResponseMessage> PostPauseAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId,
		IReadOnlyList<(long SessionId, long Version)> sessions, string? finishedAt = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=Pause");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var pairs = new List<KeyValuePair<string, string>> {
			new("LeafNodeId", leafNodeId.Value.ToString(CultureInfo.InvariantCulture)), new("__RequestVerificationToken", token),
		};
		foreach (var (sessionId, version) in sessions) {
			pairs.Add(new("endSessionId", sessionId.ToString(CultureInfo.InvariantCulture)));
			pairs.Add(new("endSessionVersion", version.ToString(CultureInfo.InvariantCulture)));
		}

		if (finishedAt is not null) {
			pairs.Add(new("finishedAt", finishedAt));
		}

		request.Content = new FormUrlEncodedContent(pairs);

		return await client.SendAsync(request);
	}

	/// <summary>
	///     Posts the write-up's own standalone Save button -- the request site.js fires ahead of any
	///     other action form whenever a #writeUp textarea is on the page, since those handlers carry no
	///     write-up fields of their own (the one-handler-one-mutation architecture rule).
	/// </summary>
	private async Task<HttpResponseMessage> PostSaveWriteUpAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId, long nodeVersion, string writeUp)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=SaveWriteUp");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["nodeVersion"] = nodeVersion.ToString(CultureInfo.InvariantCulture),
			["writeUp"] = writeUp,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<JobNodeResult> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description) =>
		await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});

	private async Task<JobNodeResult> AddWorkedLeafAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var leaf = await AddChildAsync(parentId, ownerId, description);
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
		});

		return leaf;
	}

	/// <summary>
	///     A minute-aligned UTC wall time, <paramref name="minutes" /> ago. UTC, not the test process's
	///     own local zone, because a <c>datetime-local</c> backdate posts a bare wall time with no
	///     offset and is now resolved in the *viewing employee's own* zone (<c>BackdateInstant</c>,
	///     <c>IViewerTimeZoneResolver</c>) — this suite's worker is seeded with
	///     <c>
	///         iana_time_zone =
	///         'UTC'
	///     </c>
	///     (<see cref="SeedEmployeeAsync" />), so a UTC-based wall time round-trips
	///     regardless of what zone the test process itself happens to run in.
	/// </summary>
	private static DateTimeOffset MinutesAgo(int minutes)
	{
		var now = DateTimeOffset.UtcNow;

		return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(-minutes);
	}

	private static string FormatForDateTimeLocal(DateTimeOffset value) => value.ToString(DateTimeLocalFormat, CultureInfo.InvariantCulture);

	private async Task<EquatableArray<WorkSessionResult>> GetSessionsAsync(JobNodeId leafId) =>
		await seedClient.Query.GetLeafSessionsAsync(
			new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				LeafWorkId = leafId,
			},
			CancellationToken.None);

	private async Task<HttpResponseMessage> PostAsync(
		string handler, string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId, AppUserId workedByUserId,
		string? startedAt = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/Jobs/Work?handler={handler}");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["WorkedByUserId"] = workedByUserId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (startedAt is not null) {
			fields["startedAt"] = startedAt;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostFinishAsync(
		string authCookie, string antiforgeryCookie, string token,
		JobNodeId leafNodeId, AppUserId workedByUserId, long sessionId, long version, string? finishedAt = null,
		long? nodeVersion = null, string? writeUp = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Work?handler=Finish");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["WorkedByUserId"] = workedByUserId.Value.ToString(CultureInfo.InvariantCulture),
			["sessionId"] = sessionId.ToString(CultureInfo.InvariantCulture),
			["version"] = version.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (finishedAt is not null) {
			fields["finishedAt"] = finishedAt;
		}

		if (nodeVersion is long nodeVersionValue) {
			fields["nodeVersion"] = nodeVersionValue.ToString(CultureInfo.InvariantCulture);
		}

		if (writeUp is not null) {
			fields["writeUp"] = writeUp;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostCorrectAsync(
		string authCookie, string antiforgeryCookie, string token,
		JobNodeId leafNodeId, AppUserId workedByUserId, WorkSessionId sessionId,
		string startedAt, string finishedAt, string reason)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/CorrectSession");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["WorkedByUserId"] = workedByUserId.Value.ToString(CultureInfo.InvariantCulture),
			["SessionId"] = sessionId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.StartedAt"] = startedAt,
			["Input.FinishedAt"] = finishedAt,
			["Input.Reason"] = reason,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostClearFinishAsync(
		string authCookie, string antiforgeryCookie, string token,
		JobNodeId leafNodeId, AppUserId workedByUserId, WorkSessionId sessionId,
		string startedAt, string reason)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/CorrectSession?handler=ClearFinish");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["LeafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["WorkedByUserId"] = workedByUserId.Value.ToString(CultureInfo.InvariantCulture),
			["SessionId"] = sessionId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.StartedAt"] = startedAt,
			// A finished time is still posted (the field is populated); ClearFinish must ignore it.
			["Input.FinishedAt"] = "2026-01-01T17:00",
			["Input.Reason"] = reason,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetWorkFormAsync(string authCookie, JobNodeId leafNodeId, AppUserId workedByUserId) =>
		await GetFormAsync(authCookie, $"/Jobs/Work?leafNodeId={leafNodeId.Value}&workedByUserId={workedByUserId.Value}");

	private async Task<(string CookieHeader, string Token)> GetCorrectFormAsync(
		string authCookie, JobNodeId leafNodeId, AppUserId workedByUserId, WorkSessionId sessionId) =>
		await GetFormAsync(authCookie,
			$"/Jobs/CorrectSession?leafNodeId={leafNodeId.Value}&workedByUserId={workedByUserId.Value}&sessionId={sessionId.Value}");

	private async Task<(string CookieHeader, string Token)> GetFormAsync(string authCookie, string path)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException($"No antiforgery cookie in {path} response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException($"No antiforgery token in {path} body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}



	private static (long SessionId, long Version) ExtractFirstSession(string body)
	{
		var sessionIdMatch = SessionIdPattern().Match(body);
		var versionMatch = VersionPattern().Match(body);
		if (!sessionIdMatch.Success || !versionMatch.Success) {
			throw new InvalidOperationException("No session row found in Work page body.");
		}

		return (long.Parse(sessionIdMatch.Groups["id"].Value, CultureInfo.InvariantCulture),
			long.Parse(versionMatch.Groups["version"].Value, CultureInfo.InvariantCulture));
	}



	/// <summary>
	///     Follows a redirect response, carrying forward any cookie the redirect itself set (notably
	///     the TempData cookie a mutating handler's <c>SuccessMessage</c>/<c>ErrorMessage</c> rides in
	///     on) alongside the caller's own auth cookie.
	/// </summary>
	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	[GeneratedRegex("name=\"sessionId\" value=\"(?<id>[0-9]+)\"")]
	private static partial Regex SessionIdPattern();

	[GeneratedRegex("name=\"version\" value=\"(?<version>[0-9]+)\"")]
	private static partial Regex VersionPattern();

	/// <summary>The leaf toolbar's own one-click Start button, as distinct from "Start session for worker".</summary>
	[GeneratedRegex(@">\s*Start session\s*</button>")]
	private static partial Regex OwnStartButtonPattern();

	// Both capture the submit's class across the glyph <svg> that sits between the opening tag and
	// the label -- `(?:(?!</button>).)*?` keeps the match inside the one button it started in.
	[GeneratedRegex("""<button type="submit" class="(?<class>[^"]*)"[^>]*>(?:(?!</button>).)*?Start session for worker""", RegexOptions.Singleline)]
	private static partial Regex StartForSubmitPattern();

	[GeneratedRegex("""<button type="submit" class="(?<class>[^"]*)"[^>]*>(?:(?!</button>).)*?Start session at this time""", RegexOptions.Singleline)]
	private static partial Regex BackdatedStartSubmitPattern();
}
