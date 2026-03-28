# Dokploy deployment notes

- Import the repository as a Docker Compose application.
- Use [`deploy/docker-compose.yml`](/Users/ezechiel/repos/receipt_reader/deploy/docker-compose.yml) as the stack definition.
- Persist `postgres_data` and `receipt_uploads`.
- Provide `GEMINI_API_KEY` only if Gemini enrichment should be active.
- In Dokploy, expose:
  - `receipt-web` publicly
  - `receipt-api` only if direct API access is needed
  - `receipt-ocr` as an internal service
- Before production, replace the in-memory receipt repository with PostgreSQL-backed persistence.
