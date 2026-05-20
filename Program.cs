using System.Collections.Concurrent;
using System.Text.Json;

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

var roomGraphs = new ConcurrentDictionary<string, GraphSyncPacket>();

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    IncludeFields = true,
    WriteIndented = false
};

app.MapGet("/", () => "AetherTrail Sync Server is running.");

app.MapPost("/rooms/{room}/graphs/{territoryId}", async (
    string room,
    uint territoryId,
    HttpRequest request) =>
{
    if (!IsValidRoom(room))
        return Results.BadRequest("Invalid room code.");

    if (request.ContentLength is null or > 5_000_000)
        return Results.BadRequest("Graph packet too large.");

    using var reader = new StreamReader(request.Body);
    string json = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(json))
        return Results.BadRequest("Empty graph packet.");

    GraphSyncPacket? incoming;

    try
    {
        incoming = JsonSerializer.Deserialize<GraphSyncPacket>(json, jsonOptions);
    }
    catch
    {
        return Results.BadRequest("Invalid graph packet JSON.");
    }

    if (incoming == null)
        return Results.BadRequest("Invalid graph packet.");

    if (incoming.TerritoryId != territoryId)
        return Results.BadRequest("Territory mismatch.");

    string key = BuildKey(room, territoryId);

    roomGraphs.AddOrUpdate(
        key,
        incoming,
        (_, existing) =>
        {
            MergePacket(existing, incoming);
            return existing;
        });

    int storedNodes = roomGraphs[key].Graph.Nodes.Count;

    Console.WriteLine($"UPLOAD/MERGE Room={room} Territory={territoryId} Incoming={incoming.Graph.Nodes.Count} Stored={storedNodes}");

    return Results.Ok(new
    {
        success = true,
        room,
        territoryId,
        storedNodes
    });
});

app.MapGet("/rooms/{room}/graphs/{territoryId}", (
    string room,
    uint territoryId) =>
{
    if (!IsValidRoom(room))
        return Results.BadRequest("Invalid room code.");

    string key = BuildKey(room, territoryId);

    if (!roomGraphs.TryGetValue(key, out GraphSyncPacket? packet))
        return Results.NotFound("No graph found for this room and territory.");

    Console.WriteLine($"DOWNLOAD Room={room} Territory={territoryId} Nodes={packet.Graph.Nodes.Count}");

    string json = JsonSerializer.Serialize(packet, jsonOptions);
    return Results.Content(json, "application/json");
});

app.Run();

static string BuildKey(string room, uint territoryId)
{
    return $"{room.Trim().ToUpperInvariant()}:{territoryId}";
}

static bool IsValidRoom(string room)
{
    if (string.IsNullOrWhiteSpace(room))
        return false;

    room = room.Trim().ToUpperInvariant();

    return room.Length is >= 4 and <= 12 &&
           room.All(char.IsLetterOrDigit);
}

static void MergePacket(GraphSyncPacket target, GraphSyncPacket incoming)
{
    const float mergeDistance = 2.5f;

    Dictionary<string, string> idMap = new();

    foreach (var incomingNode in incoming.Graph.Nodes)
    {
        if (!IsValidNodePosition(incomingNode.Position))
            continue;

        var existingNode = target.Graph.Nodes
            .FirstOrDefault(node => DistanceSquared(node.Position, incomingNode.Position) <= mergeDistance * mergeDistance);

        if (existingNode != null)
        {
            idMap[incomingNode.Id] = existingNode.Id;
            MergeConfidence(existingNode, incomingNode);
            continue;
        }

        string newId = $"server_{Guid.NewGuid():N}";

        var newNode = new NavNode
        {
            Id = newId,
            Position = incomingNode.Position,
            TraversalMode = incomingNode.TraversalMode,
            Links = new List<string>(),
            LinkConfidence = new Dictionary<string, int>()
        };

        target.Graph.Nodes.Add(newNode);
        idMap[incomingNode.Id] = newId;
    }

    foreach (var incomingNode in incoming.Graph.Nodes)
    {
        if (!idMap.TryGetValue(incomingNode.Id, out string? sourceId))
            continue;

        var source = target.Graph.Nodes.FirstOrDefault(node => node.Id == sourceId);

        if (source == null)
            continue;

        foreach (string incomingLinkId in incomingNode.Links)
        {
            if (!idMap.TryGetValue(incomingLinkId, out string? targetId))
                continue;

            if (sourceId == targetId)
                continue;

            var destination = target.Graph.Nodes.FirstOrDefault(node => node.Id == targetId);

            if (destination == null)
                continue;

            if (source.TraversalMode != destination.TraversalMode)
                continue;

            if (!source.Links.Contains(destination.Id))
                source.Links.Add(destination.Id);

            if (!destination.Links.Contains(source.Id))
                destination.Links.Add(source.Id);

            int confidence = incomingNode.LinkConfidence.TryGetValue(incomingLinkId, out int value)
                ? value
                : 1;

            source.LinkConfidence[destination.Id] = Math.Max(
                source.LinkConfidence.TryGetValue(destination.Id, out int existing) ? existing : 1,
                confidence
            );

            destination.LinkConfidence[source.Id] = Math.Max(
                destination.LinkConfidence.TryGetValue(source.Id, out int reverseExisting) ? reverseExisting : 1,
                confidence
            );
        }
    }
}

static void MergeConfidence(NavNode target, NavNode incoming)
{
    foreach (var pair in incoming.LinkConfidence)
    {
        if (!target.LinkConfidence.TryGetValue(pair.Key, out int existing) || pair.Value > existing)
            target.LinkConfidence[pair.Key] = pair.Value;
    }
}

static bool IsValidNodePosition(Vector3Dto position)
{
    if (float.IsNaN(position.X) || float.IsNaN(position.Y) || float.IsNaN(position.Z))
        return false;

    if (float.IsInfinity(position.X) || float.IsInfinity(position.Y) || float.IsInfinity(position.Z))
        return false;

    if (MathF.Abs(position.X) > 5000f)
        return false;

    if (MathF.Abs(position.Y) > 2000f)
        return false;

    if (MathF.Abs(position.Z) > 5000f)
        return false;

    return true;
}

static float DistanceSquared(Vector3Dto a, Vector3Dto b)
{
    float x = a.X - b.X;
    float y = a.Y - b.Y;
    float z = a.Z - b.Z;

    return x * x + y * y + z * z;
}

public sealed class GraphSyncPacket
{
    public int Version { get; set; } = 1;
    public uint TerritoryId { get; set; }
    public string SenderId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public NavGraph Graph { get; set; } = new();
}

public sealed class NavGraph
{
    public List<NavNode> Nodes { get; set; } = new();
}

public sealed class NavNode
{
    public string Id { get; set; } = "";
    public Vector3Dto Position { get; set; }
    public List<string> Links { get; set; } = new();
    public Dictionary<string, int> LinkConfidence { get; set; } = new();
    public NavTraversalMode TraversalMode { get; set; } = NavTraversalMode.Ground;
}

public enum NavTraversalMode
{
    Ground = 0,
    Flight = 1
}

public struct Vector3Dto
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}