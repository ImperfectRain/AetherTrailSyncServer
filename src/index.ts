import { DurableObject } from "cloudflare:workers";

export interface Env {
  ROOM: DurableObjectNamespace<AetherTrailRoom>;
}

type NavTraversalMode = 0 | 1;

interface Vector3Dto {
  X: number;
  Y: number;
  Z: number;
}

interface NavNode {
  Id: string;
  Position: Vector3Dto;
  Links: string[];
  LinkConfidence: Record<string, number>;
  TraversalMode: NavTraversalMode;
}

interface NavGraph {
  Nodes: NavNode[];
}

interface GraphSyncPacket {
  Version: number;
  TerritoryId: number;
  SenderId: string;
  CreatedAtUtc: string;
  Graph: NavGraph;
}

interface PartySyncPresence {
  SenderId: string;
  DisplayName: string;
  TerritoryId: number;
  Position: Vector3Dto;
  RotationRadians: number;
  UpdatedAtUtc: string;
}

interface MergeResult {
  addedNodes: number;
  mergedNodes: number;
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === "/") {
      return new Response("AetherTrail Worker Sync Server is running.");
    }

    if (url.pathname === "/version") {
      return Response.json({
        version: "worker-durable-graph-presence-v1",
        time: new Date().toISOString(),
      });
    }

    const graphMatch = url.pathname.match(/^\/rooms\/([^/]+)\/graphs\/(\d+)(?:\/stats)?$/);
    const presenceMatch = url.pathname.match(/^\/rooms\/([^/]+)\/presence\/(\d+)(?:\/sync)?$/);

    const match = graphMatch ?? presenceMatch;

    if (!match) {
      return new Response("Not found.", { status: 404 });
    }

    const room = normalizeRoom(match[1]);
    const territoryId = Number(match[2]);

    if (!isValidRoom(room)) {
      return new Response("Invalid room code.", { status: 400 });
    }

    if (!Number.isInteger(territoryId) || territoryId <= 0 || territoryId > 20000) {
      return new Response("Invalid territory.", { status: 400 });
    }

    const id = env.ROOM.idFromName(`${room}:${territoryId}`);
    const stub = env.ROOM.get(id);

    return stub.fetch(request);
  },
};

export class AetherTrailRoom extends DurableObject<Env> {
  private readonly durableState: DurableObjectState;

  constructor(state: DurableObjectState, env: Env) {
    super(state, env);
    this.durableState = state;
  }

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);

    const graphMatch = url.pathname.match(/^\/rooms\/([^/]+)\/graphs\/(\d+)(?:\/stats)?$/);
    const presenceMatch = url.pathname.match(/^\/rooms\/([^/]+)\/presence\/(\d+)(?:\/sync)?$/);

    if (graphMatch) {
      const room = normalizeRoom(graphMatch[1]);
      const territoryId = Number(graphMatch[2]);
      const isStats = url.pathname.endsWith("/stats");

      if (isStats && request.method === "GET") {
        return this.getGraphStats(room, territoryId);
      }

      if (request.method === "POST") {
        return this.uploadGraph(room, territoryId, request);
      }

      if (request.method === "GET") {
        return this.downloadGraph(room, territoryId);
      }
    }

    if (presenceMatch) {
      const room = normalizeRoom(presenceMatch[1]);
      const territoryId = Number(presenceMatch[2]);
      const isSync = url.pathname.endsWith("/sync");
    
      if (isSync && request.method === "POST") {
        return this.syncPresence(room, territoryId, request);
      }
    
      if (request.method === "POST") {
        return this.uploadPresence(room, territoryId, request);
      }
    
      if (request.method === "GET") {
        return this.downloadPresence(room, territoryId);
      }
    }

    return new Response("Not found.", { status: 404 });
  }

  private async uploadGraph(room: string, territoryId: number, request: Request): Promise<Response> {
    const contentLength = Number(request.headers.get("content-length") ?? "0");

    if (!Number.isFinite(contentLength) || contentLength <= 0 || contentLength > 5_000_000) {
      return new Response("Graph packet too large.", { status: 400 });
    }

    let incomingRaw: GraphSyncPacket;

    try {
      incomingRaw = await request.json() as GraphSyncPacket;
    } catch {
      return new Response("Invalid graph packet JSON.", { status: 400 });
    }

    if (!incomingRaw || incomingRaw.TerritoryId !== territoryId) {
      return new Response("Territory mismatch.", { status: 400 });
    }

    sanitizePacket(incomingRaw);

    if (incomingRaw.Graph.Nodes.length === 0) {
      return new Response("Packet contains no valid nodes.", { status: 400 });
    }

    const incoming = normalizePacketForServer(incomingRaw);

    const existing = await this.durableState.storage.get<GraphSyncPacket>("graph");

    let before = 0;
    let added = incoming.Graph.Nodes.length;
    let merged = 0;
    let stored = incoming.Graph.Nodes.length;
    let graphToStore = incoming;

    if (existing) {
      before = existing.Graph.Nodes.length;

      const result = mergePacket(existing, incoming);

      added = result.addedNodes;
      merged = result.mergedNodes;
      stored = existing.Graph.Nodes.length;
      graphToStore = existing;
    }

    await this.durableState.storage.put("graph", graphToStore);
    await this.durableState.storage.put("lastGraphUpdateUtc", new Date().toISOString());

    const stats = getGraphStats(graphToStore.Graph);

    console.log(
      `UPLOAD/MERGE Room=${room} Territory=${territoryId} Incoming=${incoming.Graph.Nodes.length} Before=${before} Added=${added} Merged=${merged} Stored=${stored} Bounds=(${stats.minX.toFixed(1)},${stats.minY.toFixed(1)},${stats.minZ.toFixed(1)})-(${stats.maxX.toFixed(1)},${stats.maxY.toFixed(1)},${stats.maxZ.toFixed(1)})`,
    );

    return Response.json({
      success: true,
      room,
      territoryId,
      incomingNodes: incoming.Graph.Nodes.length,
      beforeNodes: before,
      addedNodes: added,
      mergedNodes: merged,
      storedNodes: stored,
      stats,
    });
  }

  private async downloadGraph(room: string, territoryId: number): Promise<Response> {
    const packet = await this.durableState.storage.get<GraphSyncPacket>("graph");

    if (!packet) {
      return new Response("No graph found for this room and territory.", { status: 404 });
    }

    console.log(`DOWNLOAD Room=${room} Territory=${territoryId} Nodes=${packet.Graph.Nodes.length}`);

    return Response.json(packet);
  }

  private async getGraphStats(room: string, territoryId: number): Promise<Response> {
    const packet = await this.durableState.storage.get<GraphSyncPacket>("graph");
  
    if (!packet) {
      return new Response("No graph found for this room and territory.", { status: 404 });
    }
  
    const duplicateIds = findDuplicateIds(packet.Graph);
    const stats = getGraphStats(packet.Graph);
    const nodes: NavNode[] = packet.Graph.Nodes;
  
    return Response.json({
      room,
      territoryId,
      nodes: nodes.length,
      links: nodes.reduce((sum: number, node: NavNode) => sum + node.Links.length, 0),
      groundNodes: nodes.filter((node: NavNode) => node.TraversalMode === 0).length,
      flightNodes: nodes.filter((node: NavNode) => node.TraversalMode === 1).length,
      duplicateIds,
      bounds: stats,
    });
  }

  private async uploadPresence(room: string, territoryId: number, request: Request): Promise<Response> {
    const contentLength = Number(request.headers.get("content-length") ?? "0");

    if (!Number.isFinite(contentLength) || contentLength <= 0 || contentLength > 50_000) {
      return new Response("Presence packet too large.", { status: 400 });
    }

    let presence: PartySyncPresence;

    try {
      presence = await request.json() as PartySyncPresence;
    } catch {
      return new Response("Invalid presence JSON.", { status: 400 });
    }

    if (!presence || presence.TerritoryId !== territoryId) {
      return new Response("Territory mismatch.", { status: 400 });
    }

    if (!presence.SenderId || !isValidPosition(presence.Position)) {
      return new Response("Invalid presence.", { status: 400 });
    }

    presence.UpdatedAtUtc = new Date().toISOString();

    const presences = (await this.durableState.storage.get<Record<string, PartySyncPresence>>("presence")) ?? {};
    presences[presence.SenderId] = presence;

    prunePresenceMap(presences);

    await this.durableState.storage.put("presence", presences);

    console.log(
      `PRESENCE Room=${room} Territory=${territoryId} Sender=${presence.SenderId} Pos=(${presence.Position.X.toFixed(1)},${presence.Position.Y.toFixed(1)},${presence.Position.Z.toFixed(1)})`,
    );

    return Response.json({ success: true });
  }

  private async syncPresence(room: string, territoryId: number, request: Request): Promise<Response> {
    const contentLength = Number(request.headers.get("content-length") ?? "0");
  
    if (!Number.isFinite(contentLength) || contentLength <= 0 || contentLength > 50_000) {
      return new Response("Presence packet too large.", { status: 400 });
    }
  
    let presence: PartySyncPresence;
  
    try {
      presence = await request.json() as PartySyncPresence;
    } catch {
      return new Response("Invalid presence JSON.", { status: 400 });
    }
  
    if (!presence || presence.TerritoryId !== territoryId) {
      return new Response("Territory mismatch.", { status: 400 });
    }
  
    if (!presence.SenderId || !isValidPosition(presence.Position)) {
      return new Response("Invalid presence.", { status: 400 });
    }
  
    presence.UpdatedAtUtc = new Date().toISOString();
  
    const presences =
      (await this.durableState.storage.get<Record<string, PartySyncPresence>>("presence")) ?? {};
  
    presences[presence.SenderId] = presence;
  
    prunePresenceMap(presences);
  
    await this.durableState.storage.put("presence", presences);
  
    console.log(
      `PRESENCE/SYNC Room=${room} Territory=${territoryId} Sender=${presence.SenderId} Count=${Object.keys(presences).length}`
    );
  
    return Response.json(Object.values(presences));
  }

  private async downloadPresence(room: string, territoryId: number): Promise<Response> {
    const presences = (await this.durableState.storage.get<Record<string, PartySyncPresence>>("presence")) ?? {};

    prunePresenceMap(presences);

    await this.durableState.storage.put("presence", presences);

    return Response.json(Object.values(presences));
  }
}

function normalizeRoom(room: string): string {
  return room.trim().toUpperCase();
}

function isValidRoom(room: string): boolean {
  return /^[A-Z0-9]{4,12}$/.test(room);
}

function sanitizePacket(packet: GraphSyncPacket): void {
  packet.Graph ??= { Nodes: [] };
  packet.Graph.Nodes ??= [];

  const maxNodes = 25_000;
  const maxLinksPerNode = 24;
  const maxConfidence = 100;

  packet.Graph.Nodes = packet.Graph.Nodes
    .filter((node) =>
      node &&
      typeof node.Id === "string" &&
      node.Id.trim().length > 0 &&
      isValidPosition(node.Position),
    )
    .slice(0, maxNodes);

  const validIds = new Set(packet.Graph.Nodes.map((node) => node.Id));

  for (const node of packet.Graph.Nodes) {
    node.Links ??= [];
    node.LinkConfidence ??= {};
    node.TraversalMode = node.TraversalMode === 1 ? 1 : 0;

    node.Links = [...new Set(node.Links)]
      .filter((linkId) =>
        typeof linkId === "string" &&
        linkId !== node.Id &&
        validIds.has(linkId),
      )
      .slice(0, maxLinksPerNode);

    for (const key of Object.keys(node.LinkConfidence)) {
      if (!node.Links.includes(key)) {
        delete node.LinkConfidence[key];
        continue;
      }

      node.LinkConfidence[key] = clampNumber(node.LinkConfidence[key], 1, maxConfidence);
    }

    for (const linkId of node.Links) {
      node.LinkConfidence[linkId] ??= 1;
    }
  }
}

function normalizePacketForServer(packet: GraphSyncPacket): GraphSyncPacket {
  const firstIdMap = new Map<string, string>();
  const exactNodeMap = new Map<string, string>();

  const normalized: GraphSyncPacket = {
    Version: packet.Version || 1,
    TerritoryId: packet.TerritoryId,
    SenderId: packet.SenderId ?? "",
    CreatedAtUtc: packet.CreatedAtUtc ?? new Date().toISOString(),
    Graph: { Nodes: [] },
  };

  for (const node of packet.Graph.Nodes) {
    const serverId = `server_${crypto.randomUUID().replace(/-/g, "")}`;

    const normalizedNode: NavNode = {
      Id: serverId,
      Position: node.Position,
      TraversalMode: node.TraversalMode === 1 ? 1 : 0,
      Links: [],
      LinkConfidence: {},
    };

    normalized.Graph.Nodes.push(normalizedNode);

    exactNodeMap.set(buildExactNodeKey(node), serverId);

    if (!firstIdMap.has(node.Id)) {
      firstIdMap.set(node.Id, serverId);
    }
  }

  const normalizedById = new Map(normalized.Graph.Nodes.map((node) => [node.Id, node]));

  for (const originalNode of packet.Graph.Nodes) {
    const sourceServerId = exactNodeMap.get(buildExactNodeKey(originalNode));

    if (!sourceServerId) {
      continue;
    }

    const source = normalizedById.get(sourceServerId);

    if (!source) {
      continue;
    }

    for (const originalLinkId of originalNode.Links) {
      const targetServerId = firstIdMap.get(originalLinkId);

      if (!targetServerId || targetServerId === sourceServerId) {
        continue;
      }

      const destination = normalizedById.get(targetServerId);

      if (!destination) {
        continue;
      }

      if (source.TraversalMode !== destination.TraversalMode) {
        continue;
      }

      if (!isTraversableLink(source.Position, destination.Position)) {
        continue;
      }

      addLink(source, destination);

      const confidence = clampNumber(originalNode.LinkConfidence[originalLinkId] ?? 1, 1, 100);

      source.LinkConfidence[destination.Id] = Math.max(source.LinkConfidence[destination.Id] ?? 1, confidence);
      destination.LinkConfidence[source.Id] = Math.max(destination.LinkConfidence[source.Id] ?? 1, confidence);
    }
  }

  return normalized;
}

function buildExactNodeKey(node: NavNode): string {
  return `${node.Id}|${node.Position.X}|${node.Position.Y}|${node.Position.Z}|${node.TraversalMode}`;
}

function mergePacket(target: GraphSyncPacket, incoming: GraphSyncPacket): MergeResult {
  const mergeDistance = 0.75;

  const idMap = new Map<string, string>();
  const targetById = buildNodeLookup(target.Graph);

  let addedNodes = 0;
  let mergedNodes = 0;

  for (const incomingNode of incoming.Graph.Nodes) {
    if (!isValidPosition(incomingNode.Position)) {
      continue;
    }

    const existingNode = findNearestCompatibleNode(target.Graph, incomingNode, mergeDistance);

    if (existingNode) {
      idMap.set(incomingNode.Id, existingNode.Id);
      mergedNodes++;
      continue;
    }

    const newId = `server_${crypto.randomUUID().replace(/-/g, "")}`;

    const newNode: NavNode = {
      Id: newId,
      Position: incomingNode.Position,
      TraversalMode: incomingNode.TraversalMode,
      Links: [],
      LinkConfidence: {},
    };

    target.Graph.Nodes.push(newNode);
    targetById.set(newId, newNode);
    idMap.set(incomingNode.Id, newId);
    addedNodes++;
  }

  for (const incomingNode of incoming.Graph.Nodes) {
    const mappedSourceId = idMap.get(incomingNode.Id);

    if (!mappedSourceId) {
      continue;
    }

    const source = targetById.get(mappedSourceId);

    if (!source) {
      continue;
    }

    for (const incomingLinkId of incomingNode.Links) {
      const mappedTargetId = idMap.get(incomingLinkId);

      if (!mappedTargetId || mappedSourceId === mappedTargetId) {
        continue;
      }

      const destination = targetById.get(mappedTargetId);

      if (!destination) {
        continue;
      }

      if (source.TraversalMode !== destination.TraversalMode) {
        continue;
      }

      if (!isTraversableLink(source.Position, destination.Position)) {
        continue;
      }

      addLink(source, destination);

      const confidence = clampNumber(incomingNode.LinkConfidence[incomingLinkId] ?? 1, 1, 100);

      source.LinkConfidence[destination.Id] = Math.max(source.LinkConfidence[destination.Id] ?? 1, confidence);
      destination.LinkConfidence[source.Id] = Math.max(destination.LinkConfidence[source.Id] ?? 1, confidence);
    }
  }

  return { addedNodes, mergedNodes };
}

function buildNodeLookup(graph: NavGraph): Map<string, NavNode> {
  const lookup = new Map<string, NavNode>();

  for (const node of graph.Nodes) {
    if (!lookup.has(node.Id)) {
      lookup.set(node.Id, node);
    }
  }

  return lookup;
}

function findNearestCompatibleNode(graph: NavGraph, incomingNode: NavNode, maxDistance: number): NavNode | null {
  const maxDistanceSq = maxDistance * maxDistance;

  let best: NavNode | null = null;
  let bestDistanceSq = maxDistanceSq;

  for (const node of graph.Nodes) {
    if (node.TraversalMode !== incomingNode.TraversalMode) {
      continue;
    }

    const distanceSq = distanceSquared(node.Position, incomingNode.Position);

    if (distanceSq < bestDistanceSq) {
      bestDistanceSq = distanceSq;
      best = node;
    }
  }

  return best;
}

function addLink(a: NavNode, b: NavNode): void {
  if (!a.Links.includes(b.Id)) {
    a.Links.push(b.Id);
  }

  if (!b.Links.includes(a.Id)) {
    b.Links.push(a.Id);
  }

  a.LinkConfidence[b.Id] ??= 1;
  b.LinkConfidence[a.Id] ??= 1;
}

function mergeNodeConfidence(target: NavNode, incoming: NavNode): void {
  for (const [key, value] of Object.entries(incoming.LinkConfidence)) {
    const confidence = clampNumber(value, 1, 100);

    if ((target.LinkConfidence[key] ?? 0) < confidence) {
      target.LinkConfidence[key] = confidence;
    }
  }
}

function isValidPosition(position: Vector3Dto | undefined): position is Vector3Dto {
  if (!position) {
    return false;
  }

  const values = [position.X, position.Y, position.Z];

  return values.every((value) =>
    typeof value === "number" &&
    Number.isFinite(value),
  ) &&
    Math.abs(position.X) <= 5000 &&
    Math.abs(position.Y) <= 2000 &&
    Math.abs(position.Z) <= 5000;
}

function isTraversableLink(a: Vector3Dto, b: Vector3Dto): boolean {
  const maxLinkDistance = 30.0;
  const maxVerticalDelta = 10.0;
  const maxSlopeRatio = 1.5;

  const dx = b.X - a.X;
  const dy = b.Y - a.Y;
  const dz = b.Z - a.Z;

  const totalDistanceSq = dx * dx + dy * dy + dz * dz;

  if (totalDistanceSq > maxLinkDistance * maxLinkDistance) {
    return false;
  }

  const verticalDelta = Math.abs(dy);

  if (verticalDelta > maxVerticalDelta) {
    return false;
  }

  const horizontalDistance = Math.sqrt(dx * dx + dz * dz);

  if (horizontalDistance < 0.1) {
    return verticalDelta <= 1.5;
  }

  return verticalDelta / horizontalDistance <= maxSlopeRatio;
}

function distanceSquared(a: Vector3Dto, b: Vector3Dto): number {
  const x = a.X - b.X;
  const y = a.Y - b.Y;
  const z = a.Z - b.Z;

  return x * x + y * y + z * z;
}

function clampNumber(value: number, min: number, max: number): number {
  if (!Number.isFinite(value)) {
    return min;
  }

  return Math.max(min, Math.min(max, value));
}

function getGraphStats(graph: NavGraph) {
  if (graph.Nodes.length === 0) {
    return {
      minX: 0,
      minY: 0,
      minZ: 0,
      maxX: 0,
      maxY: 0,
      maxZ: 0,
    };
  }

  return {
    minX: Math.min(...graph.Nodes.map((node) => node.Position.X)),
    minY: Math.min(...graph.Nodes.map((node) => node.Position.Y)),
    minZ: Math.min(...graph.Nodes.map((node) => node.Position.Z)),
    maxX: Math.max(...graph.Nodes.map((node) => node.Position.X)),
    maxY: Math.max(...graph.Nodes.map((node) => node.Position.Y)),
    maxZ: Math.max(...graph.Nodes.map((node) => node.Position.Z)),
  };
}

function findDuplicateIds(graph: NavGraph) {
  const counts = new Map<string, number>();

  for (const node of graph.Nodes) {
    counts.set(node.Id, (counts.get(node.Id) ?? 0) + 1);
  }

  return [...counts.entries()]
    .filter(([, count]) => count > 1)
    .map(([id, count]) => ({ id, count }));
}

function prunePresenceMap(presences: Record<string, PartySyncPresence>): void {
  const cutoff = Date.now() - 45_000;

  for (const [senderId, presence] of Object.entries(presences)) {
    const updated = Date.parse(presence.UpdatedAtUtc);

    if (!Number.isFinite(updated) || updated < cutoff) {
      delete presences[senderId];
    }
  }
}