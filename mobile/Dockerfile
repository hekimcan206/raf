# syntax=docker/dockerfile:1
# Expo web build served by an unprivileged nginx that also proxies /api and /hubs to the API (one origin: no CORS,
# and the app's same-origin default API URL just works).

FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN --mount=type=cache,target=/root/.npm npm ci --no-audit --no-fund
COPY . .
RUN npx expo export --platform web --output-dir dist

FROM nginxinc/nginx-unprivileged:1.29-alpine AS web
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
