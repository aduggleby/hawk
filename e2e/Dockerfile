## <file>
## <summary>
## Docker image for running Playwright E2E tests in headed Chromium via Xvfb.
## Outputs screenshots to /work/screenshots (volume mounted from host).
## </summary>
## </file>

FROM mcr.microsoft.com/playwright:v1.58.2-jammy

WORKDIR /work

COPY e2e/package.json e2e/package-lock.json ./
RUN npm ci

COPY e2e/playwright.config.ts ./playwright.config.ts
COPY e2e/tests ./tests

# Ensure screenshots directory exists in the container even if not mounted.
RUN mkdir -p /work/screenshots

ENV SCREENSHOTS_DIR=/work/screenshots

ENTRYPOINT ["bash", "-lc"]
CMD ["until curl -fsS \"$HAWK_BASE_URL/\" >/dev/null; do echo \"waiting for web: $HAWK_BASE_URL\"; sleep 1; done; Xvfb :99 -screen 0 1280x720x24 -nolisten tcp & export DISPLAY=:99; npx playwright test --project=chromium --reporter=list"]
