# ---- Stage 1: build the Vue app ----
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm ci || npm install
COPY . .
RUN npm run build

# ---- Stage 2: serve with nginx ----
FROM nginx:1.27-alpine
# nginx.conf is an envsubst TEMPLATE: the official image's entrypoint expands any
# ${ENV_VAR} in /etc/nginx/templates/*.template into /etc/nginx/conf.d/ at startup.
# Only real env vars (ATLAS_UPSTREAM) are substituted; nginx's own $host/$uri are kept.
COPY nginx.conf /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html

# Backend the /api proxy points at. Override at run time:
#   docker run -e ATLAS_UPSTREAM=host.docker.internal:8096 -p 8080:80 qubic-atlas
ENV ATLAS_UPSTREAM=host.docker.internal:8099

EXPOSE 80
# (default nginx CMD / entrypoint runs the template expansion then starts nginx)
