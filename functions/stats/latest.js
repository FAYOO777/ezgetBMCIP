import { corsHeaders, getLatestStats, json } from "../_shared/downloads.js";

export async function onRequest(context) {
    const { request, env } = context;

    if (request.method === "OPTIONS") {
        return new Response(null, { headers: corsHeaders() });
    }

    if (request.method !== "GET" && request.method !== "HEAD") {
        return json({ error: "Method not allowed" }, { status: 405, headers: corsHeaders() });
    }

    const stats = await getLatestStats(env);
    return json(stats, { headers: corsHeaders() });
}
