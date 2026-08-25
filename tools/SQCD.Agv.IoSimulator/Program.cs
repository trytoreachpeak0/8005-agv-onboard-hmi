using SQCD.Agv.IoSimulator;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["IoSimulator:url"] ?? "http://127.0.0.1:58006");
builder.Services.AddSingleton<IoSimulatorState>();

WebApplication app = builder.Build();
app.MapGet("/health", (IoSimulatorState state) => state.Fault.Mode == SimulatorFaultMode.Crashed
    ? Results.Json(new { status = "unavailable", mode = state.Fault.Mode.ToString() }, statusCode: 503)
    : Results.Ok(new { status = "live", mode = state.Fault.Mode.ToString() }));
app.MapGet("/api/v1/slots", async (IoSimulatorState state, CancellationToken cancellationToken) =>
{
    IResult? fault = await state.BeforeRequestAsync(isMutation: false, cancellationToken);
    return fault ?? Results.Ok(state.Snapshot());
});
app.MapPost("/api/v1/unlock", async (
    UnlockRequest request,
    IoSimulatorState state,
    CancellationToken cancellationToken) =>
{
    IResult? fault = await state.BeforeRequestAsync(isMutation: true, cancellationToken);
    if (fault is not null && state.Fault.Mode != SimulatorFaultMode.ResponseLost)
    {
        return fault;
    }
    state.PulseUnlock(request.SlotNumbers);
    return fault ?? Results.Ok(new { accepted = true, slotNumbers = request.SlotNumbers.Distinct().Order() });
});
app.MapPut("/api/v1/slots/{slotNo:int}", (
    int slotNo,
    SlotMutation mutation,
    IoSimulatorState state) =>
{
    state.Update(slotNo, mutation);
    return Results.Ok(state.Snapshot().Single(item => item.SlotNo == slotNo));
});
app.MapPost("/api/v1/control/fault", (FaultRequest request, IoSimulatorState state) =>
{
    state.SetFault(request);
    return Results.Ok(new { mode = state.Fault.Mode.ToString(), state.Fault.SlotNo, state.Fault.DelayMs });
});
app.MapPost("/api/v1/control/recover", (IoSimulatorState state) =>
{
    state.Recover();
    return Results.Ok(new { mode = SimulatorFaultMode.None.ToString() });
});

await app.RunAsync();

public partial class Program;
