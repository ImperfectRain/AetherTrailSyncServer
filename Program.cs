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
var roomLocks = new ConcurrentDictionary<string, object>();

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

    if (territoryId == 0 || territoryId > 20_000)
        return Results.BadRequest("Invalid territory.");

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
    catch (Exception ex)
    {
        Console.WriteLine($"BAD JSON Room={room} Territory={territoryId} Error={ex.Message}");
        return Results.BadRequest("Invalid graph packet JSON.");
    }

    if (incoming == null)
        return Results.BadRequest("Invalid graph packet.");

    if (incoming.TerritoryId != territoryId)
        return Results.BadRequest("Territory mismatch.");

    SanitizePacket(incoming);

    if (incoming.Graph.Nodes.Count == 0)
        return Results.BadRequest("Packet contains no valid nodes.");

    string key = BuildKey(room, territoryId);
    object roomLock = roomLocks.GetOrAdd(key, _ => new object());

    int before;
    int incomingCount = incoming.Graph.Nodes.Count;
    int added;
    int merged;
    int stored;

    lock (roomLock)
    {
        if (!roomGraphs.TryGetValue(key, out GraphSyncPacket? existing))
        {
            roomGraphs[key] = incoming;
            before = 0;
            added = incoming.Graph.Nodes.Count;
            merged = 0;
            stored = incoming.Graph.Nodes.Count;
        }
        else
        {
            before = existing.Graph.Nodes.Count;

            var result = MergePacket(existing, incoming);

            added = result.AddedNodes;
            merged = result.MergedNodes;
            stored = existing.Graph.Nodes.Count;
        }
    }

    var first = incoming.Graph.Nodes.FirstOrDefault();

    if (first != null)
    {
        Console.WriteLine(
            $"UPLOAD/MERGE Room={room.Trim().ToUpperInvariant()} Territory={territoryId} " +
            $"Incoming={incomingCount} Before={before} Added={added} Merged={merged} Stored={stored} " +
            $"FirstPos=({first.Position.X:F2},{first.Position.Y:F2},{first.Position.Z:F2})"
        );
    }
    else
    {
        Console.WriteLine(
            $"UPLOAD/MERGE Room={room.Trim().ToUpperInvariant()} Territory={territoryId} " +
            $"Incoming={incomingCount} Before={before} Added={added} Merged={merged} Stored={stored}"
        );
    }

    return Results.Ok(new
    {
        success = true,
        room = room.Trim().ToUpperInvariant(),
        territoryId,
        incomingNodes = incomingCount,
        beforeNodes = before,
        addedNodes = added,
        mergedNodes = merged,
        storedNodes = stored
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

    Console.WriteLine($"DOWNLOAD Room={room.Trim().ToUpperInvariant()} Territory={territoryId} Nodes={packet.Graph.Nodes.Count}");

    string json = JsonSerializer.Serialize(packet, jsonOptions);
    return Results.Content(json, "application/json");
});

app.MapGet("/rooms/{room}/graphs/{territoryId}/stats", (
    string room,
    uint territoryId) =>
{
    if (!IsValidRoom(room))
        return Results.BadRequest("Invalid room code.");

    string key = BuildKey(room, territoryId);

    if (!roomGraphs.TryGetValue(key, out GraphSyncPacket? packet))
        return Results.NotFound("No graph found for this room and territory.");

    return Results.Ok(new
    {
        room = room.Trim().ToUpperInvariant(),
        territoryId,
        nodes = packet.Graph.Nodes.Count,
        links = packet.Graph.Nodes.Sum(node => node.Links.Count),
        groundNodes = packet.Graph.Nodes.Count(node => node.TraversalMode == NavTraversalMode.Ground),
        flightNodes = packet.Graph.Nodes.Count(node => node.TraversalMode == NavTraversalMode.Flight),
        firstNode = packet.Graph.Nodes.FirstOrDefault()
    });
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

static void SanitizePacket(GraphSyncPacket packet)
{
    const int maxNodes = 25_000;
    const int maxLinksPerNode = 16;
    const int maxConfidence = 100;

    packet.Graph ??= new NavGraph();
    packet.Graph.Nodes ??= new List<NavNode>();

    packet.Graph.Nodes = packet.Graph.Nodes
        .Where(node =>
            node != null &&
            !string.IsNullOrWhiteSpace(node.Id) &&
            IsValidNodePosition(node.Position))
        .Take(maxNodes)
        .ToList();

    var validIds = packet.Graph.Nodes
        .Select(node => node.Id)
        .ToHashSet();

    foreach (var node in packet.Graph.Nodes)
    {
        node.Links ??= new List<string>();
        node.LinkConfidence ??= new Dictionary<string, int>();

        node.Links = node.Links
            .Where(linkId =>
                !string.IsNullOrWhiteSpace(linkId) &&
                linkId != node.Id &&
                validIds.Contains(linkId))
            .Distinct()
            .Take(maxLinksPerNode)
            .ToList();

        foreach (var key in node.LinkConfidence.Keys.ToList())
        {
            if (!node.Links.Contains(key))
            {
                node.LinkConfidence.Remove(key);
                continue;
            }

            node.LinkConfidence[key] = Math.Clamp(node.LinkConfidence[key], 1, maxConfidence);
        }

        foreach (string linkId in node.Links)
        {
            if (!node.LinkConfidence.ContainsKey(linkId))
                node.LinkConfidence[linkId] = 1;
        }
    }
}

static MergeResult MergePacket(GraphSyncPacket target, GraphSyncPacket incoming)
{
    const float mergeDistance = 0.75f;

    Dictionary<string, string> idMap = new();
    Dictionary<string, NavNode> targetById = target.Graph.Nodes.ToDictionary(node => node.Id);

    int addedNodes = 0;
    int mergedNodes = 0;

    foreach (var incomingNode in incoming.Graph.Nodes)
    {
        if (!IsValidNodePosition(incomingNode.Position))
            continue;

        var existingNode = FindNearestCompatibleNode(
            target.Graph,
            incomingNode,
            mergeDistance
        );

        if (existingNode != null)
        {
            idMap[incomingNode.Id] = existingNode.Id;
            MergeNodeConfidence(existingNode, incomingNode);
            mergedNodes++;
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
        targetById[newId] = newNode;
        idMap[incomingNode.Id] = newId;
        addedNodes++;
    }

    foreach (var incomingNode in incoming.Graph.Nodes)
    {
        if (!idMap.TryGetValue(incomingNode.Id, out string? mappedSourceId))
            continue;

        if (!targetById.TryGetValue(mappedSourceId, out var source))
            continue;

        foreach (string incomingLinkId in incomingNode.Links)
        {
            if (!idMap.TryGetValue(incomingLinkId, out string? mappedTargetId))
                continue;

            if (mappedSourceId == mappedTargetId)
                continue;

            if (!targetById.TryGetValue(mappedTargetId, out var destination))
                continue;

            if (source.TraversalMode != destination.TraversalMode)
                continue;

            if (!IsTraversableLink(source.Position, destination.Position))
                continue;

            AddLink(source, destination);

            int confidence = incomingNode.LinkConfidence.TryGetValue(incomingLinkId, out int value)
                ? value
                : 1;

            confidence = Math.Clamp(confidence, 1, 100);

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

    return new MergeResult(addedNodes, mergedNodes);
}

static NavNode? FindNearestCompatibleNode(NavGraph graph, NavNode incomingNode, float maxDistance)
{
    float maxDistanceSq = maxDistance * maxDistance;

    NavNode? best = null;
    float bestDistanceSq = maxDistanceSq;

    foreach (var node in graph.Nodes)
    {
        if (node.TraversalMode != incomingNode.TraversalMode)
            continue;

        float distanceSq = DistanceSquared(node.Position, incomingNode.Position);

        if (distanceSq < bestDistanceSq)
        {
            bestDistanceSq = distanceSq;
            best = node;
        }
    }

    return best;
}

static void AddLink(NavNode a, NavNode b)
{
    if (!a.Links.Contains(b.Id))
        a.Links.Add(b.Id);

    if (!b.Links.Contains(a.Id))
        b.Links.Add(a.Id);

    if (!a.LinkConfidence.ContainsKey(b.Id))
        a.LinkConfidence[b.Id] = 1;

    if (!b.LinkConfidence.ContainsKey(a.Id))
        b.LinkConfidence[a.Id] = 1;
}

static void MergeNodeConfidence(NavNode target, NavNode incoming)
{
    foreach (var pair in incoming.LinkConfidence)
    {
        if (!target.LinkConfidence.TryGetValue(pair.Key, out int existing) || pair.Value > existing)
            target.LinkConfidence[pair.Key] = Math.Clamp(pair.Value, 1, 100);
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

static bool IsTraversableLink(Vector3Dto a, Vector3Dto b)
{
    const float maxLinkDistance = 30.0f;
    const float maxVerticalDelta = 10.0f;
    const float maxSlopeRatio = 1.5f;

    float dx = b.X - a.X;
    float dy = b.Y - a.Y;
    float dz = b.Z - a.Z;

    float totalDistanceSq = dx * dx + dy * dy + dz * dz;

    if (totalDistanceSq > maxLinkDistance * maxLinkDistance)
        return false;

    float verticalDelta = MathF.Abs(dy);

    if (verticalDelta > maxVerticalDelta)
        return false;

    float horizontalDistance = MathF.Sqrt(dx * dx + dz * dz);

    if (horizontalDistance < 0.1f)
        return verticalDelta <= 1.5f;

    return verticalDelta / horizontalDistance <= maxSlopeRatio;
}

static float DistanceSquared(Vector3Dto a, Vector3Dto b)
{
    float x = a.X - b.X;
    float y = a.Y - b.Y;
    float z = a.Z - b.Z;

    return x * x + y * y + z * z;
}

public sealed record MergeResult(int AddedNodes, int MergedNodes);

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