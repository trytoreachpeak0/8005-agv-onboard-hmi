using System.Runtime.CompilerServices;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ScreenState;
using Xunit;
using Harness = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Harness;
using ScriptStep = SQCD.Agv.WireToGateG2Tests.FakeIoModuleClient.ScriptStep;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 多篮装货的「剩下的不装（已装 k/N 篮）」（8005-agv-onboard-hmi#388，CP-0021 REQ-0367，协议 6.0.0 候选）：已装 k≥1 篮、另一仓
/// 空关锁闭等「重新开门」时给出；按下后申请服务端，在途时不开任何门、不能再按；授权后按 k 篮收尾报
/// <c>COMPLETED_REMAINING_SKIPPED</c>，拒绝后说明原因、照常继续。
/// </summary>
/// <remarks>
/// <para>
/// <b>现场形状。</b>10-21 agv03 @160：需求 <c>ed84063a</c> 装 5、6 两仓，5 号仓已装进一篮，6 号仓开过锁、空关并已锁闭，等着
/// 「重新开门」。替身服务端的命令用 1、2 号仓，仓号不同不影响判定；1 号仓装进一篮，2 号仓空关。
/// </para>
/// <para>
/// <b>取红。</b>票面要求的注入：去掉授权等待——车在按下「剩下的不装」时不等服务端，自己按 k 篮收尾。这时
/// <see cref="NothingOpensAndNothingEndsWhileTheRequestWaitsForTheServer"/> 看到在途期间就有了 <c>OperationResult</c>。
/// </para>
/// </remarks>
public sealed class RemainingBasketsSkipG2Tests
{
    private static readonly int[] TwoSlots = [1, 2];

    /// <summary>The harness's operation timeout, the prompt cadence.</summary>
    private static readonly TimeSpan PromptCadence = TimeSpan.FromSeconds(2);

    static RemainingBasketsSkipG2Tests()
    {
        // The harness reads its credential and operator id from variables that class's static constructor sets.
        RuntimeHelpers.RunClassConstructor(typeof(MultiDemandJourneyG2Tests).TypeHandle);
    }

    /// <summary>
    /// The agv03 @160 shape: not offered while the first door is open; offered once slot 1 holds its basket and slot 2 is
    /// shut empty, in the user's words; the press is authorized and the load ends with the one basket, slot 2 skipped, no
    /// door opened after the press.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task Agv03At160TheSkipIsOfferedAndAnAuthorizationEndsTheLoadWithTheLoadedBasket()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = LoadsSlotOneThenShutsSlotTwoEmpty();
        await using Harness harness = await StartAsync(io, token, server => server.RespondToRemainingBasketsSkips = true);

        await WaitUntilAsync(harness, () => io.UnlockCount == 1 && Pending(harness) is null, "slot 1 open", token);
        Assert.False(harness.OnUi(() => harness.ViewModel.ShowsRemainingBasketsSkip));

        WireToGateRemainingBasketsSkipOffer offer = await WaitForOfferAsync(harness, token);
        Assert.Equal([1], offer.LoadedSlots);
        Assert.Equal(2, offer.ExpectedBasketCount);
        Assert.Equal(2, offer.HeldSlot);
        Assert.Equal("剩下的不装（已装 1/2 篮）", harness.OnUi(() => harness.ViewModel.RemainingBasketsSkipCaption));
        Assert.Contains(
            "已装的 1 篮照常送走，剩下的 1 篮留在机台，系统不再管",
            WireToGateRemainingBasketsSkipText.Confirmation(offer.LoadedSlots.Count, offer.ExpectedBasketCount),
            StringComparison.Ordinal);
        HmiScreenState screen = harness.ScreenState();
        HmiButtonState button = Assert.Single(screen.Buttons, item => item.Key == "OnRemainingBasketsSkipClick");
        Assert.True(button.Visible);
        Assert.True(button.Enabled);
        Assert.Equal("OFFERED:1/2", Assert.Single(screen.RawCodes, code => code.AutomationId == "RemainingBasketsSkip").Value);
        // 「重新开门」 stays on offer beside it: the operator may still load the second basket.
        Assert.True(harness.OnUi(() => harness.ViewModel.CanReopenOperation));

        Assert.True(await Press(harness, offer));

        JsonElement result = await WaitForResultAsync(harness, token);
        Assert.Equal("COMPLETED_REMAINING_SKIPPED", result.GetProperty("overallOutcome").GetString());
        JsonElement[] slots = [.. result.GetProperty("slotResults").EnumerateArray()];
        JsonElement loaded = Assert.Single(slots, slot => slot.GetProperty("slotNo").GetInt32() == 1);
        Assert.Equal("COMPLETED", loaded.GetProperty("outcome").GetString());
        Assert.Equal("OCCUPIED", loaded.GetProperty("finalPhysicalState").GetString());
        JsonElement skipped = Assert.Single(slots, slot => slot.GetProperty("slotNo").GetInt32() == 2);
        Assert.Equal("NOT_STARTED", skipped.GetProperty("outcome").GetString());
        Assert.Equal("EMPTY", skipped.GetProperty("finalPhysicalState").GetString());
        Assert.Equal("LOCKED", skipped.GetProperty("lockState").GetString());
        Assert.Equal("RESET", skipped.GetProperty("unlockOutputState").GetString());
        Assert.Equal(["REMAINING_BASKETS_SKIPPED"], ReasonCodes(skipped));
        Assert.Equal(2, io.UnlockCount);

        // The request went out before the result, with the loaded slot and the operator the vehicle knew.
        string[] order = [.. harness.Server.ReceivedEnvelopes
            .Select(envelope => envelope.MessageType)
            .Where(type => type is "RemainingBasketsSkipRequested" or "OperationResult")];
        Assert.Equal(["RemainingBasketsSkipRequested", "OperationResult"], order);
        JsonElement request = Assert.Single(harness.Server.RemainingBasketsSkipRequests).Payload;
        Assert.Equal([1], request.GetProperty("loadedSlots").EnumerateArray().Select(slot => slot.GetInt32()));
        Assert.Equal(JsonValueKind.Object, request.GetProperty("operator").ValueKind);
        await WaitUntilAsync(
            harness,
            () => !harness.OnUi(() => harness.ViewModel.ShowsRemainingBasketsSkip),
            "the button gone once the load has ended",
            token);
    }

    /// <summary>
    /// While the press waits for the server: no door opens, 「重新开门」 is taken away and refused, the button stays on screen
    /// but cannot be pressed again, and nothing ends the load -- for a whole prompt cadence and more. Then the answer comes
    /// and the load ends. The red of the ticket's injection: a vehicle that does not wait has a result here already.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-AUTHORIZED")]
    public async Task NothingOpensAndNothingEndsWhileTheRequestWaitsForTheServer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = LoadsSlotOneThenShutsSlotTwoEmpty();
        await using Harness harness = await StartAsync(io, token);

        WireToGateRemainingBasketsSkipOffer offer = await WaitForOfferAsync(harness, token);
        Assert.True(await Press(harness, offer));

        await WaitUntilAsync(
            harness,
            () => harness.OnUi(() => harness.ViewModel.RemainingBasketsSkipStatus) == "PENDING:1/2",
            "the press pending on screen",
            token);
        await Task.Delay(PromptCadence + TimeSpan.FromMilliseconds(500), token);
        AssertNoResult(harness);
        Assert.Equal(2, io.UnlockCount);
        HmiButtonState button = Assert.Single(harness.ScreenState().Buttons, item => item.Key == "OnRemainingBasketsSkipClick");
        Assert.True(button.Visible);
        Assert.False(button.Enabled);
        Assert.False(harness.OnUi(() => harness.ViewModel.CanReopenOperation));
        Assert.False(await harness.Business.RequestSlotReopenAsync(token));
        Assert.False(await Press(harness, offer));
        Assert.Single(harness.Server.RemainingBasketsSkipRequests);

        string skipRequestId = harness.Server.RemainingBasketsSkipRequests[0].Payload.GetProperty("skipRequestId").GetString()!;
        await harness.Server.SendRemainingBasketsSkipResultAsync(skipRequestId, "AUTHORIZED", [1], null);

        JsonElement result = await WaitForResultAsync(harness, token);
        Assert.Equal("COMPLETED_REMAINING_SKIPPED", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(2, io.UnlockCount);
    }

    /// <summary>
    /// A rejection is shown with its reason, loading goes on: 「重新开门」 is back, the skip may be pressed again as a new
    /// request, and the second basket loaded after the reopen completes the load normally.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-REJECTED")]
    public async Task ARejectionIsShownAndLoadingGoesOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = LoadsSlotOneThenShutsSlotTwoEmpty();
        io.ScriptPulse(3, OpensThenShut(cargo: true));
        await using Harness harness = await StartAsync(
            io,
            token,
            server =>
            {
                server.RespondToRemainingBasketsSkips = true;
                server.RejectRemainingBasketsSkipsWith = "LOCK_NOT_CLOSED";
            });

        Assert.True(await Press(harness, await WaitForOfferAsync(harness, token)));

        await WaitUntilAsync(
            harness,
            () => harness.Events.Any(item => item.Kind == "REMAINING_BASKETS_SKIP_REJECTED"),
            "the rejection shown",
            token);
        Assert.Contains(
            harness.Events,
            item => item.Kind == "REMAINING_BASKETS_SKIP_REJECTED"
                && item.Message.Contains("有仓门没有确认锁好", StringComparison.Ordinal)
                && item.Message.Contains("LOCK_NOT_CLOSED", StringComparison.Ordinal));
        WireToGateRemainingBasketsSkipOffer again = await WaitForOfferAsync(harness, token);
        Assert.Equal([1], again.LoadedSlots);
        AssertNoResult(harness);
        Assert.True(harness.OnUi(() => harness.ViewModel.CanReopenOperation));
        Assert.True(await harness.OnUi(() => harness.ViewModel.RequestReopenCurrentOperationAsync()));

        JsonElement result = await WaitForResultAsync(harness, token);
        Assert.Equal("COMPLETED", result.GetProperty("overallOutcome").GetString());
        Assert.All(result.GetProperty("slotResults").EnumerateArray(), slot => Assert.Equal("COMPLETED", slot.GetProperty("outcome").GetString()));
        Assert.Equal(3, io.UnlockCount);
        AssertNeverSaid(harness, "REMAINING_BASKETS_SKIPPED");
    }

    /// <summary>
    /// Not offered over the first slot shut empty (k = 0), however long it waits; offered once a basket is in, and a refusal
    /// for nothing loaded is shown and changes nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-NOTHING-LOADED")]
    public async Task NothingIsOfferedBeforeABasketIsLoaded()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(1, OpensThenShut(cargo: false));
        io.ScriptPulse(2, OpensThenShut(cargo: true));
        io.ScriptPulse(3, OpensThenShut(cargo: false));
        await using Harness harness = await StartAsync(
            io,
            token,
            server =>
            {
                server.RespondToRemainingBasketsSkips = true;
                server.RejectRemainingBasketsSkipsWith = "REMAINING_BASKETS_SKIP_NOTHING_LOADED";
            });

        await WaitUntilAsync(
            harness,
            () => io.UnlockCount == 1 && harness.OnUi(() => harness.ViewModel.CanReopenOperation),
            "slot 1 held shut empty",
            token);
        await Task.Delay(PromptCadence + TimeSpan.FromMilliseconds(500), token);
        Assert.False(harness.OnUi(() => harness.ViewModel.ShowsRemainingBasketsSkip));
        Assert.Null(harness.Business.RemainingBasketsSkipOffer);
        Assert.Equal(
            string.Empty,
            Assert.Single(harness.ScreenState().RawCodes, code => code.AutomationId == "RemainingBasketsSkip").Value ?? string.Empty);
        Assert.True(await harness.OnUi(() => harness.ViewModel.RequestReopenCurrentOperationAsync()));

        Assert.True(await Press(harness, await WaitForOfferAsync(harness, token)));
        await WaitUntilAsync(
            harness,
            () => harness.Events.Any(item => item.Kind == "REMAINING_BASKETS_SKIP_REJECTED"),
            "the refusal shown",
            token);
        Assert.Contains(
            harness.Events,
            item => item.Message.Contains("服务端看到这条任务还没有装进花篮", StringComparison.Ordinal));
        AssertNoResult(harness);
        Assert.Equal(3, io.UnlockCount);
    }

    /// <summary>
    /// A link that drops with the request unanswered: the next session resubmits it under a new messageId with the same
    /// <c>skipRequestId</c> and the same payload; the server's answer is applied once.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-DUPLICATE-REQUEST")]
    public async Task AReconnectResubmitsTheSameRequestUnderANewMessageId()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = LoadsSlotOneThenShutsSlotTwoEmpty();
        await using Harness harness = await StartAsync(
            io,
            token,
            server =>
            {
                server.RespondToRemainingBasketsSkips = true;
                server.RemainingBasketsSkipAcksToDrop = 1;
            });

        Assert.True(await Press(harness, await WaitForOfferAsync(harness, token)));
        await WaitUntilAsync(harness, () => harness.Server.RemainingBasketsSkipRequests.Count == 1, "the first request", token);
        harness.Server.CloseLatestConnection();
        await harness.Session.Client.ConnectAndRecoverAsync(token);

        JsonElement result = await WaitForResultAsync(harness, token);
        Assert.Equal("COMPLETED_REMAINING_SKIPPED", result.GetProperty("overallOutcome").GetString());
        (string MessageId, JsonElement Payload)[] requests = [.. harness.Server.RemainingBasketsSkipRequests];
        Assert.True(requests.Length >= 2, $"expected a resubmission, saw {requests.Length}");
        Assert.Equal(requests.Length, requests.Select(request => request.MessageId).Distinct().Count());
        Assert.All(requests, request => Assert.Equal(requests[0].Payload.GetRawText(), request.Payload.GetRawText()));
        Assert.Single(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageType == "OperationResult");
        Assert.Equal(2, io.UnlockCount);
    }

    /// <summary>
    /// A <c>ProtocolProblem</c> answering the request drops it -- no result follows one -- and the operator may press again:
    /// a new request with a new <c>skipRequestId</c>, which is authorized.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-DUPLICATE-REQUEST")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task AProtocolProblemDropsTheRequestAndAnotherPressIsANewOne()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = LoadsSlotOneThenShutsSlotTwoEmpty();
        await using Harness harness = await StartAsync(
            io,
            token,
            server =>
            {
                server.RespondToRemainingBasketsSkips = true;
                server.NextRemainingBasketsSkipProtocolProblem = "BUSINESS_ID_CONTENT_CONFLICT";
            });

        Assert.True(await Press(harness, await WaitForOfferAsync(harness, token)));
        await WaitUntilAsync(
            harness,
            () => harness.Events.Any(item => item.Kind == "REMAINING_BASKETS_SKIP_DROPPED"),
            "the request dropped",
            token);
        AssertNoResult(harness);
        await WaitUntilAsync(
            harness,
            () => harness.OnUi(() => harness.ViewModel.CanReopenOperation),
            "「重新开门」 back once the request is dropped",
            token);

        Assert.True(await Press(harness, await WaitForOfferAsync(harness, token)));
        JsonElement result = await WaitForResultAsync(harness, token);
        Assert.Equal("COMPLETED_REMAINING_SKIPPED", result.GetProperty("overallOutcome").GetString());
        string[] ids = [.. harness.Server.RemainingBasketsSkipRequests
            .Select(request => request.Payload.GetProperty("skipRequestId").GetString()!)];
        Assert.Equal(2, ids.Distinct().Count());
    }

    /// <summary>
    /// The load ends another way while the request waits -- the held slot turns unreadable, decision 2, UNKNOWN -- so the
    /// request is dropped; the server's <c>AUTHORIZED</c> that comes after is acknowledged and not acted on: no second result,
    /// no slot reported skipped.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-UNAUTHORIZED-RESULT")]
    public async Task AnAuthorizationAfterTheLoadEndedAnotherWayIsNotActedOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = LoadsSlotOneThenShutsSlotTwoEmpty();
        await using Harness harness = await StartAsync(io, token);

        Assert.True(await Press(harness, await WaitForOfferAsync(harness, token)));
        await WaitUntilAsync(harness, () => harness.Server.RemainingBasketsSkipRequests.Count == 1, "the request", token);
        io.SetUnreadable(1);

        JsonElement result = await WaitForResultAsync(harness, token);
        Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString());
        await WaitUntilAsync(
            harness,
            () => harness.Events.Any(item => item.Kind == "REMAINING_BASKETS_SKIP_DROPPED"),
            "the request dropped with the load",
            token);

        string skipRequestId = harness.Server.RemainingBasketsSkipRequests[0].Payload.GetProperty("skipRequestId").GetString()!;
        string resultMessageId = Guid.NewGuid().ToString("D");
        await harness.Server.SendRemainingBasketsSkipResultAsync(skipRequestId, "AUTHORIZED", [1], null, resultMessageId);
        await WaitUntilAsync(
            harness,
            () => harness.Server.ReceivedEnvelopes.Any(envelope => envelope.MessageType == "DurableAck"
                && envelope.WireLine.Contains(resultMessageId, StringComparison.Ordinal)),
            "the late authorization acknowledged",
            token);
        Assert.Single(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageType == "OperationResult");
        AssertNeverSaid(harness, "REMAINING_BASKETS_SKIPPED", "COMPLETED_REMAINING_SKIPPED");
    }

    /// <summary>
    /// The process goes away with the request unanswered. The next process does not settle the load from the live IO at
    /// once: it resubmits the request (a new messageId, the same payload), and once the authorization is on file reports the
    /// skip -- without opening a door.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-DUPLICATE-REQUEST")]
    public async Task ARestartWithTheRequestPendingResubmitsItAndSettlesOnTheAnswer()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = LoadsSlotOneThenShutsSlotTwoEmpty();
        await using Harness first = await StartAsync(io, token);

        Assert.True(await Press(first, await WaitForOfferAsync(first, token)));
        await WaitUntilAsync(first, () => first.Server.RemainingBasketsSkipRequests.Count == 1, "the request", token);
        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();
        first.Server.RespondToRemainingBasketsSkips = true;

        await using Harness second = await Harness.StartAgainstAsync(
            first.Server,
            token,
            first.JournalPath,
            io,
            operationTimeout: PromptCadence);

        JsonElement result = await WaitForResultAsync(second, token);
        Assert.Equal("COMPLETED_REMAINING_SKIPPED", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(2, io.UnlockCount);
        (string MessageId, JsonElement Payload)[] requests = [.. second.Server.RemainingBasketsSkipRequests];
        Assert.True(requests.Length >= 2, $"expected a resubmission after the restart, saw {requests.Length}");
        Assert.NotEqual(requests[0].MessageId, requests[^1].MessageId);
        Assert.Equal(requests[0].Payload.GetRawText(), requests[^1].Payload.GetRawText());
    }

    /// <summary>
    /// The same restart, and the resubmission is answered with a <c>ProtocolProblem</c>: the request is dropped, and the
    /// interrupted settlement it held back runs at once -- without waiting for a reconnect -- and reports the load as it
    /// always has after a restart, UNKNOWN, never as skipped (review M1 of PR #406).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-REMAINING-BASKETS-SKIP-DUPLICATE-REQUEST")]
    public async Task ARestartWhoseResubmissionIsRefusedStillSettlesTheLoad()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = LoadsSlotOneThenShutsSlotTwoEmpty();
        await using Harness first = await StartAsync(io, token);

        Assert.True(await Press(first, await WaitForOfferAsync(first, token)));
        await WaitUntilAsync(first, () => first.Server.RemainingBasketsSkipRequests.Count == 1, "the request", token);
        await first.StopVehicleAsync();
        first.Server.SimulateOnboardProcessRestart();
        first.Server.NextRemainingBasketsSkipProtocolProblem = "BUSINESS_ID_CONTENT_CONFLICT";
        first.Server.RespondToRemainingBasketsSkips = true;

        await using Harness second = await Harness.StartAgainstAsync(
            first.Server,
            token,
            first.JournalPath,
            io,
            operationTimeout: PromptCadence);

        await WaitUntilAsync(
            second,
            () => second.Events.Any(item => item.Kind == "REMAINING_BASKETS_SKIP_DROPPED"),
            "the resubmission dropped by the ProtocolProblem",
            token);
        JsonElement result = await WaitForResultAsync(second, token);
        Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(2, io.UnlockCount);
        AssertNeverSaid(second, "REMAINING_BASKETS_SKIPPED", "COMPLETED_REMAINING_SKIPPED");
    }

    /// <summary>
    /// A cancellation asked for with a basket in is refused by the server with <c>LOAD_CANCELLATION_BASKET_LOADED</c>: the
    /// screen says so and names the way to load fewer, the skip is still offered, and the load goes on.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BASKET-LOADED")]
    public async Task ACancellationRefusedOnceABasketIsLoadedPointsToTheSkip()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = LoadsSlotOneThenShutsSlotTwoEmpty();
        await using Harness harness = await StartAsync(
            io,
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationDecision = "REJECTED";
                server.LoadCancellationRejectionReasonCode = "LOAD_CANCELLATION_BASKET_LOADED";
            });

        await WaitForOfferAsync(harness, token);
        _ = await harness.Business.RequestLoadCancellationAsync("现场不装了。", cancellationToken: token);

        await WaitUntilAsync(
            harness,
            () => harness.Events.Any(item => item.Message.Contains("LOAD_CANCELLATION_BASKET_LOADED", StringComparison.Ordinal)),
            "the refusal shown",
            token);
        Assert.Contains(
            harness.Events,
            item => item.Message.Contains("已经装进花篮，不能取消；要少装请按「剩下的不装」", StringComparison.Ordinal));
        WireToGateRemainingBasketsSkipOffer offer = await WaitForOfferAsync(harness, token);
        Assert.Equal([1], offer.LoadedSlots);
        AssertNoResult(harness);
        Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, envelope => envelope.MessageType == "LoadCancellationResult");
    }

    /// <summary>Pulse 1 opens slot 1 and the operator puts a basket in; pulse 2 opens slot 2 and it is shut empty.</summary>
    private static FakeIoModuleClient LoadsSlotOneThenShutsSlotTwoEmpty()
    {
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(1, OpensThenShut(cargo: true));
        io.ScriptPulse(2, OpensThenShut(cargo: false));
        return io;
    }

    private static ScriptStep[] OpensThenShut(bool cargo) =>
    [
        new(TimeSpan.FromMilliseconds(40), (fake, slot) => fake.SetLockFeedback(slot, locked: false)),
        new(TimeSpan.FromMilliseconds(600), (fake, slot) => fake.SetUnlockOutput(slot, energised: false)),
        new(TimeSpan.FromMilliseconds(1200), (fake, slot) => fake.ShutDoor(slot, cargo)),
    ];

    private static async Task<Harness> StartAsync(
        FakeIoModuleClient io,
        CancellationToken token,
        Action<FakeControlServer>? configure = null) =>
        await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.SendSlotOperationCommandAfterRecovery = true;
                server.SlotOperationType = "LOAD";
                server.SlotOperationSlots = TwoSlots;
                configure?.Invoke(server);
            },
            token,
            io: io,
            operationTimeout: PromptCadence);

    private static Task<bool> Press(Harness harness, WireToGateRemainingBasketsSkipOffer offer) =>
        harness.OnUi(() => harness.ViewModel.RequestRemainingBasketsSkipAsync(offer));

    private static string? Pending(Harness harness) => harness.Business.RemainingBasketsSkipOffer is null ? null : "offered";

    /// <summary>Waits for the button to be offered, or for a result -- which fails the test with the result's own words.</summary>
    private static async Task<WireToGateRemainingBasketsSkipOffer> WaitForOfferAsync(Harness harness, CancellationToken token)
    {
        await WaitUntilAsync(
            harness,
            () => ResultLine(harness) is not null
                || harness.OnUi(() => harness.ViewModel.CanRequestRemainingBasketsSkip)
                && harness.OnUi(() => harness.ViewModel.RemainingBasketsSkipOffer) is not null,
            "「剩下的不装」 offered, or a result",
            token);
        AssertNoResult(harness);
        return harness.OnUi(() => harness.ViewModel.RemainingBasketsSkipOffer)!;
    }

    private static async Task<JsonElement> WaitForResultAsync(Harness harness, CancellationToken token)
    {
        await WaitUntilAsync(harness, () => ResultLine(harness) is not null, "the OperationResult", token);
        using JsonDocument document = JsonDocument.Parse(ResultLine(harness)!);
        return document.RootElement.GetProperty("payload").Clone();
    }

    private static string? ResultLine(Harness harness) =>
        harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "OperationResult")
            .Select(envelope => envelope.WireLine)
            .FirstOrDefault();

    private static void AssertNoResult(Harness harness)
    {
        string? result = ResultLine(harness);
        Assert.True(result is null, $"The load ended (pulses so far: {harness.Io.UnlockCount}): {result}");
    }

    private static void AssertNeverSaid(Harness harness, params string[] fragments) =>
        Assert.All(fragments, fragment => Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.WireLine.Contains(fragment, StringComparison.Ordinal)));

    private static string[] ReasonCodes(JsonElement slotResult) =>
        [.. slotResult.GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()!)];

    private static async Task WaitUntilAsync(
        Harness harness,
        Func<bool> predicate,
        string expectation,
        CancellationToken token)
    {
        StallAwareDeadline deadline = new(TimeSpan.FromSeconds(20));
        while (!predicate())
        {
            if (deadline.HasExpired)
            {
                Assert.Fail(
                    $"Timed out after {deadline.Describe()} waiting for: {expectation}. Pulses: {harness.Io.UnlockCount}."
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, harness.Events.Select(item => $"{item.Kind}: {item.Message}"))
                    + Environment.NewLine
                    + "Received: " + string.Join(", ", harness.Server.ReceivedEnvelopes.Select(envelope => envelope.MessageType))
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, harness.Logger.Tail(30)));
            }

            await deadline.PollAsync(token);
        }
    }
}
