# OpenClicky backend as a container (the VPS has Docker but no Node).
# Build context is the repo root (the backend is an npm workspace and reads skills/ + app-skills/):
#   docker build -f backend/Dockerfile -t openclicky-backend .
FROM node:22-alpine AS build
WORKDIR /repo
COPY package.json package-lock.json tsconfig.base.json ./
COPY backend/package.json backend/
COPY agent/package.json agent/
RUN npm ci --workspace backend --include-workspace-root --ignore-scripts
COPY backend backend
COPY skills skills
COPY app-skills app-skills
RUN npm run build -w backend

FROM node:22-alpine
ENV NODE_ENV=production PORT=8787
WORKDIR /repo
COPY --from=build /repo/package.json /repo/package-lock.json ./
COPY --from=build /repo/backend/package.json backend/
COPY --from=build /repo/node_modules node_modules
COPY --from=build /repo/backend/dist backend/dist
USER node
EXPOSE 8787
CMD ["node", "backend/dist/node.js"]
