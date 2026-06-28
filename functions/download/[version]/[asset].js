import {
    corsHeaders,
    incrementDownload,
    isDownloadAsset,
    json,
    r2PublicBase,
    safeSegment,
} from "../../_shared/downloads.js";

export async function onRequest(context) {
    const { request, env, params } = context;
    const version = params.version;
    const asset = params.asset;

    if (request.method === "OPTIONS") {
        return new Response(null, { headers: corsHeaders() });
    }

    if (request.method !== "GET" && request.method !== "HEAD") {
        return json({ error: "Method not allowed" }, { status: 405, headers: corsHeaders() });
    }

    if (!safeSegment(version) || !safeSegment(asset) || !isDownloadAsset(asset)) {
        return json({ error: "Invalid download path" }, { status: 400, headers: corsHeaders() });
    }

    const target = `${r2PublicBase(env)}/${version}/${asset}`;

    if (request.method === "GET") {
        context.waitUntil(incrementDownload(env, version, asset));
    }

    return Response.redirect(target, 302);
}
