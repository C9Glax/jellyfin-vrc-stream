# Container Configuration

| Variable | Description | Default |
|----------|-------------|---------|
| `JELLYFIN_URL` | Jellyfin server URL | `http://jellyfin:8096` |
| `JELLYFIN_API_KEY` | Jellyfin API key - also doubles as the proxy's own admin credential (send as `X-Admin-Key` header or `admin_key` query param) for admin/browsing endpoints and creating share links. If unset, the VRC Share plugin can provide one automatically via `POST /pair` (see below) instead of setting this manually | (none - disables those endpoints until paired) |
| `CACHE_DIR` | HLS cache directory. Also where an auto-paired admin key (from `POST /pair`) is persisted as `.paired_admin_key.json` - use durable storage here if you rely on pairing instead of `JELLYFIN_API_KEY` | `/tmp/hls-cache` |
| `PUBLIC_BASE_URL` | External base URL used to build share link URLs | (falls back to request base URL) |
| `DEFAULT_SHARE_TTL_SECONDS` | Default share link lifetime in seconds | `86400` (24h) |
| `STREAM_IDLE_TIMEOUT` | Cleanup streams idle for N seconds (0=disable) | `300` (5 min) |
| `CLEANUP_INTERVAL` | Run cleanup every N seconds (0=disable) | `60` |
| `MAX_CACHE_SIZE_MB` | Max cache size in MB (0=disable) | `1800` (1.8 GB) |
| **Quality Settings** | | |
| `VIDEO_BITRATE` | Video bitrate in bits/sec | `40000000` (40 Mbps) |
| `AUDIO_BITRATE` | Audio bitrate in bits/sec | `320000` (320 Kbps) |
| `MAX_STREAMING_BITRATE` | Total bitrate cap in bits/sec | `50000000` (50 Mbps) |
| `MAX_WIDTH` | Maximum video width | `1920` |
| `MAX_HEIGHT` | Maximum video height | `1080` |
| `MAX_FRAMERATE` | Maximum framerate | `60` |
| `H264_PROFILE` | H.264 profile (baseline/main/high) | `high` |
| `H264_LEVEL` | H.264 level (41=1080p30, 42=1080p60) | `41` |
| `MAX_REF_FRAMES` | Reference frames for motion quality | `4` |

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