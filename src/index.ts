export interface Env {
  ROOM: DurableObjectNamespace<AetherTrailRoom>;
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === "/") {
      return new Response("AetherTrail Worker Sync Server is running.");
    }

    if (url.pathname === "/version") {
      return Response.json({
        version: "worker-durable-object-test-v1",
        time: new Date().toISOString(),
      });
    }

    const match = url.pathname.match(/^\/rooms\/([^/]+)\/test\/(\d+)$/);

    if (match) {
      const room = match[1].trim().toUpperCase();
      const territoryId = match[2];

      const id = env.ROOM.idFromName(`${room}:${territoryId}`);
      const stub = env.ROOM.get(id);

      return stub.fetch(request);
    }

    return new Response("Not found.", { status: 404 });
  },
};

export class AetherTrailRoom {
  private state: DurableObjectState;
  private env: Env;

  constructor(state: DurableObjectState, env: Env) {
    this.state = state;
    this.env = env;
  }

  async fetch(request: Request): Promise<Response> {
    const count = ((await this.state.storage.get<number>("count")) ?? 0) + 1;

    await this.state.storage.put("count", count);

    return Response.json({
      ok: true,
      message: "Durable Object room is working.",
      count,
      time: new Date().toISOString(),
    });
  }
}