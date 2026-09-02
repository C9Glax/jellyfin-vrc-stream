# Container Configuration

| Variable | Description | Default |
|----------|-------------|---------|
| `JELLYFIN_URL` | Jellyfin server URL | `http://jellyfin:8096` |
| `JELLYFIN_API_KEY` | Jellyfin API key - also doubles as the proxy's own admin credential (send as `X-Admin-Key` header or `admin_key` query param) for admin/browsing endpoints and creating share links. If unset, the VRC Share plugin can provide one automatically via `POST /pair` (see below) instead of setting this manually | (none - disables those endpoints until paired) |
| `CACHE_DIR` | HLS cache directory. Also where an auto-paired admin key (from `POST /pair`) is persisted as `.paired_admin_key.json` - use durable storage here if you rely on pairing instead of `JELLYFIN_API_KEY` | `/tmp/hls-cache` |
| `PUBLIC_BASE_URL` | External base URL used to build share link URLs | (falls back to request base URL) |
| `DEFAULT_SHARE_TTL_SECONDS` | Default share link lifetime in seconds | `86400` (24h) |
| `STREAM_IDLE_TIMEOUT` | Cleanup streams idle for N seconds (0=disable) | `300` (5 min) |
| `LOCKED_STREAM_IDLE_TIMEOUT` | Same, but for streams locked to stay warm (see `/streams/{key}/lock`) | `86400` (24h) |
| `CLEANUP_INTERVAL` | Run cleanup every N seconds (0=disable) | `60` |
| `MAX_CACHE_SIZE_MB` | Max cache size in MB (0=disable) | `1800` (1.8 GB) |

`STREAM_IDLE_TIMEOUT`, `LOCKED_STREAM_IDLE_TIMEOUT`, `CLEANUP_INTERVAL` and
`MAX_CACHE_SIZE_MB` are just the initial defaults - they can be changed at
runtime via `GET`/`PUT /settings` (admin-key protected, same as `/profiles`
below), including from the VRC Share plugin's config page. A change made
this way is persisted to `.runtime_settings.json` in `CACHE_DIR` and
overrides the env vars above on every subsequent restart, so it needs the
same durable-storage caveat as the paired admin key (see below) to survive a
restart. Switching `CLEANUP_INTERVAL` from `0` to a positive value this way
still requires a restart to actually start the cleanup task; other changes
take effect immediately.

## Encoding / quality settings

Encoding is not configured via environment variables. It's a set of named
quality profiles (built-in presets plus any custom ones you create) managed
through the `/profiles` REST API (`GET`/`POST`/`PUT`/`DELETE`, admin-key
protected) or the VRC Share plugin's config page, which lists, creates,
edits and deletes them for you. Each profile carries `video_bitrate`,
`audio_bitrate`, `max_streaming_bitrate`, `max_width`, `max_height`,
`max_framerate`, `h264_profile`, `h264_level` and `max_ref_frames`.

## Pairing instead of manually setting `JELLYFIN_API_KEY`

If `JELLYFIN_API_KEY` is left unset, the VRC Share Jellyfin plugin can mint a
key itself and hand it to the proxy via `POST /pair` - no need to create a
key by hand and paste it into two places. This is trust-on-first-use: only
the first `/pair` call succeeds (subsequent calls get `409` until an admin
who already has the current key calls `DELETE /pair` to reset it). If
`CACHE_DIR` isn't durable (e.g. the sample `deployment/kubernetes.yaml` mounts
it as a memory-backed `emptyDir`), the paired key is lost on restart and the
plugin must re-pair - either give `CACHE_DIR` persistent storage, or keep
setting `JELLYFIN_API_KEY` explicitly for that deployment.