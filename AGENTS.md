# Repository rules

- CacheAnalyzer is analysis-only. Preserve API request and response headers, payloads, and status codes. Never emit an ETag, implement conditional responses, suppress a body, rewrite Content-Type/Content-Length, or introduce caching. Fingerprints are internal analysis data; existing API caching behavior belongs to the application. Buffering must preserve content and restore readable request positions. Document buffering overhead and compatibility limits rather than claiming zero impact on timing or streaming.

- Do not introduce application-level locking anywhere in this repository: no `lock`, `Monitor`, mutexes, reader/writer locks, semaphores, or equivalent serialization to make analysis consistent.
- Cache analysis is advisory. Prefer concurrent collections such as `ConcurrentDictionary` and `ConcurrentQueue`, with point-in-time snapshots. Approximate recommendations and capacity races are accepted; do not make requests wait on each other for exact history accounting.
- This rule permits the built-in synchronization inside concurrent collections. Do not claim that their use makes the entire implementation formally lock-free or guarantees zero waiting.
- Compare all eligible fingerprints in the snapshot with the current fingerprint; do not assume the current observation occupies the last position under concurrency.
- Keep history retention bounded on a best-effort basis, document concurrency limitations, and test eventual/sequential behavior rather than requiring atomic consistency.
