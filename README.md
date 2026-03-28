# Receipt Reader

Skeleton application for scanning paper receipts from a phone:

- `apps/receipt-reader-web` - Angular mobile-first PWA UI
- `apps/ReceiptReader.Api` - ASP.NET Core API orchestrating storage, OCR, parsing and Gemini enrichment
- `services/receipt-ocr` - Go OCR HTTP service with a stable contract and Tesseract fallback behavior
- `deploy/docker-compose.yml` - local Docker and Dokploy-friendly stack definition

## Architecture

1. Angular uploads a receipt image with `multipart/form-data`.
2. .NET API stores the original image and enqueues a processing job.
3. Go OCR service performs the base OCR pass and returns normalized lines plus confidence hints.
4. .NET parser extracts merchant, tax id, date, totals and line items.
5. Gemini can optionally refine uncertain fields, but the backend remains authoritative for totals and validation.

## Local run

### API

```bash
dotnet restore apps/ReceiptReader.Api/ReceiptReader.Api.csproj
dotnet run --project apps/ReceiptReader.Api
```

### OCR service

```bash
go run ./services/receipt-ocr
```

### Angular UI

```bash
cd apps/receipt-reader-web
npm install
npm start
```

### Docker

```bash
cd deploy
docker compose up --build
```

## Notes

- The API currently uses an in-memory repository to keep the skeleton light; the contract is prepared for a later PostgreSQL persistence layer.
- The Go OCR service uses Tesseract when available and falls back to deterministic sample OCR text so the end-to-end flow stays usable in development.
- Gemini is optional and disabled by default. Set `GEMINI_API_KEY` and `Gemini__Enabled=true` to enable enrichment.
