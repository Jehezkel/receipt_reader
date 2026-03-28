FROM node:24-bookworm-slim AS build
WORKDIR /app

COPY apps/receipt-reader-web/package*.json ./
RUN npm install

COPY apps/receipt-reader-web/ ./
RUN npm run build

FROM nginx:1.27-alpine
WORKDIR /usr/share/nginx/html

COPY apps/receipt-reader-web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/receipt-reader-web/browser ./
EXPOSE 80
