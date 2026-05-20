export default {
    async fetch(request: Request): Promise<Response> {
      const url = new URL(request.url);
  
      if (url.pathname === "/") {
        return new Response("AetherTrail Worker Sync Server is running.");
      }
  
      if (url.pathname === "/version") {
        return Response.json({
          version: "worker-test-v1",
          time: new Date().toISOString()
        });
      }
  
      return new Response("Not found.", { status: 404 });
    }
  };