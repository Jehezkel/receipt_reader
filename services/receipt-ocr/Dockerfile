FROM golang:1.26 AS build
WORKDIR /src

COPY services/receipt-ocr/go.mod ./
COPY services/receipt-ocr/main.go ./
RUN go build -o /app/receipt-ocr .

FROM debian:bookworm-slim
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates tesseract-ocr tesseract-ocr-pol \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/receipt-ocr /app/receipt-ocr
ENV PORT=8080
ENV OCR_LANGUAGE_HINT=pol+eng
EXPOSE 8080

ENTRYPOINT ["/app/receipt-ocr"]
