using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();

var roomGraphs = new ConcurrentDictionary<string, string>();

app.MapGet("/", () => "AetherTrail Sync Server is running.");

app.MapPost("/rooms/{room}/graphs/{territoryId}", async (
    string room,
    uint territoryId,
    HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    string json = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(json))
        return Results.BadRequest("Empty graph packet.");

    string key = BuildKey(room, territoryId);

    roomGraphs[key] = json;

    Console.WriteLine($"UPLOAD Room={room} Territory={territoryId} Bytes={json.Length}");

    return Results.Ok(new
    {
        success = true,
        room,
        territoryId
    });
});

app.MapGet("/rooms/{room}/graphs/{territoryId}", (
    string room,
    uint territoryId) =>
{
    string key = BuildKey(room, territoryId);

    if (!roomGraphs.TryGetValue(key, out string? json))
        return Results.NotFound("No graph found for this room and territory.");

    Console.WriteLine($"DOWNLOAD Room={room} Territory={territoryId}");

    return Results.Content(json, "application/json");
});

app.Run();

static string BuildKey(string room, uint territoryId)
{
    return $"{room.Trim().ToUpperInvariant()}:{territoryId}";
}