# Dokploy deployment notes

- Import the repository as a Docker Compose application.
- Use [`deploy/docker-compose.yml`](/Users/ezechiel/repos/receipt_reader/deploy/docker-compose.yml) as the stack definition.
- Persist `postgres_data` and `receipt_uploads`.
- Provide `GEMINI_API_KEY` only if Gemini enrichment should be active.
- Recommended Dokploy layout:
  - expose `receipt-web` publicly
  - keep `receipt-api` internal
  - keep `receipt-ocr` internal
  - keep `postgres` internal
- The web container proxies `/api` and `/uploads` to `receipt-api`, so the frontend can use the same public origin and usually does not need browser CORS at all.
- If you decide to expose `receipt-api` publicly on a separate domain, set `CORS_ALLOWED_ORIGINS` to the exact web origin, for example `https://your-web-domain.example`.
- With Dokploy and a random Traefik domain, you can leave `CORS_ALLOWED_ORIGINS` empty as long as only `receipt-web` is public.
- Before production, replace the in-memory receipt repository with PostgreSQL-backed persistence.
