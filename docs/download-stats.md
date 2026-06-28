# Download stats

The download page supports anonymous download counters through Cloudflare Pages Functions.

## Routes

- `/download/:version/:asset`
  - Increments the counter for the requested zip file.
  - Redirects to the real R2 public file URL.
- `/stats/latest`
  - Returns the latest counter snapshot as JSON.

## Cloudflare binding

Create a KV namespace and bind it to the Pages project with this exact binding name:

```text
DOWNLOAD_STATS
```

Optional environment variable:

```text
R2_PUBLIC_BASE=https://dl.fayoo.fun
```

If omitted, the functions use `https://dl.fayoo.fun`.

The functions are intentionally tolerant of a missing binding. Downloads still redirect, and the page simply hides counters if stats are unavailable.

## Stored keys

```text
downloads:v1.3.4:ezgetBMCIP-full.zip = 12
downloads:v1.3.4:ezgetBMCIP-lite.zip = 8
downloads:latest = {"v1.3.4":{"ezgetBMCIP-full.zip":12}}
```

The counters are anonymous and grouped only by version and file name.
