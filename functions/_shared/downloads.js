const DEFAULT_R2_PUBLIC_BASE = "https://dl.fayoo.fun";

function json(data, init = {}) {
    return new Response(JSON.stringify(data), {
        ...init,
        headers: {
            "content-type": "application/json; charset=utf-8",
            "cache-control": "no-store",
            ...(init.headers || {}),
        },
    });
}

function corsHeaders() {
    return {
        "access-control-allow-origin": "*",
        "access-control-allow-methods": "GET, HEAD, OPTIONS",
        "access-control-allow-headers": "content-type",
    };
}

function safeSegment(value) {
    return typeof value === "string" && /^[A-Za-z0-9._-]+$/.test(value);
}

function isDownloadAsset(asset) {
    return /^ezgetBMCIP-[A-Za-z0-9._-]+\.zip$/.test(asset);
}

function statKey(version, asset) {
    return `downloads:${version}:${asset}`;
}

function latestStatsKey() {
    return "downloads:latest";
}

async function incrementDownload(env, version, asset) {
    if (!env.DOWNLOAD_STATS) return;

    const key = statKey(version, asset);
    const latestKey = latestStatsKey();

    const [currentValue, latestValue] = await Promise.all([
        env.DOWNLOAD_STATS.get(key),
        env.DOWNLOAD_STATS.get(latestKey, "json"),
    ]);

    const count = (Number.parseInt(currentValue || "0", 10) || 0) + 1;
    const latest = latestValue && typeof latestValue === "object" ? latestValue : {};
    latest[version] = latest[version] && typeof latest[version] === "object" ? latest[version] : {};
    latest[version][asset] = count;

    await Promise.all([
        env.DOWNLOAD_STATS.put(key, String(count)),
        env.DOWNLOAD_STATS.put(latestKey, JSON.stringify(latest)),
    ]);
}

async function getLatestStats(env) {
    if (!env.DOWNLOAD_STATS) return {};
    const latest = await env.DOWNLOAD_STATS.get(latestStatsKey(), "json");
    return latest && typeof latest === "object" ? latest : {};
}

export {
    corsHeaders,
    getLatestStats,
    incrementDownload,
    isDownloadAsset,
    json,
    r2PublicBase,
    safeSegment,
};
function r2PublicBase(env) {
    return (env.R2_PUBLIC_BASE || DEFAULT_R2_PUBLIC_BASE).replace(/\/+$/, "");
}
