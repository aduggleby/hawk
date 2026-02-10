# Hawk

ASP.NET Razor Pages uptime checker and URL verifier with Hangfire scheduling, SQL Server storage, and Docker-first deployment. Styled with Tailwind CSS v4 and supports dark mode.

## Install On TrueNAS SCALE (Custom App YAML)

These instructions target TrueNAS SCALE's **Apps** feature using a **Custom App** where you paste Kubernetes YAML.

### Prereqs

- TrueNAS SCALE with Apps enabled.
- A way to provide persistent storage for:
  - SQL Server data
  - Hawk DataProtection keys (keeps login sessions valid across restarts)
  - Optional: logs
- You must choose a StorageClass that exists on your system (replace `YOUR_STORAGE_CLASS`).

### 1) Choose Credentials And Settings

You will set:

- `SA_PASSWORD`: SQL Server `sa` password (must be strong).
- `SEED_ADMIN_EMAIL` and `SEED_ADMIN_PASSWORD`: initial Hawk admin user credentials.
- Optional email settings (Resend-compatible):
  - `RESEND_BASEURL`
  - `RESEND_APIKEY`
  - `EMAIL_FROM`

Note:
- Hawk applies EF Core migrations automatically on startup.
- Default web port inside the container is `8080`.

### 2) Create The App (Paste YAML)

In TrueNAS SCALE:

1. Apps -> Discover Apps -> Custom App.
2. Set an app name (e.g. `hawk`).
3. Paste the YAML below into the YAML editor.
4. Replace:
   - `YOUR_STORAGE_CLASS`
   - passwords
   - (optional) image names/tags
5. Deploy.

```yaml
apiVersion: v1
kind: Namespace
metadata:
  name: hawk
---
apiVersion: v1
kind: Secret
metadata:
  name: hawk-secrets
  namespace: hawk
type: Opaque
stringData:
  SA_PASSWORD: "CHANGE_ME_strong_sa_password"
  SEED_ADMIN_EMAIL: "ad@dualconsult.com"
  SEED_ADMIN_PASSWORD: "CHANGE_ME_strong_admin_password"
  RESEND_APIKEY: "dev"
---
apiVersion: v1
kind: ConfigMap
metadata:
  name: hawk-config
  namespace: hawk
data:
  # If you run Hawk behind an ingress, keep this true and terminate TLS at the ingress.
  Hawk__DisableHttpsRedirection: "true"

  # Resend-compatible settings (point at Resend or a compatible gateway).
  RESEND_BASEURL: "https://api.resend.com"
  EMAIL_FROM: "Hawk <hawk@yourdomain>"
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: hawk-mssql-data
  namespace: hawk
spec:
  accessModes:
    - ReadWriteOnce
  resources:
    requests:
      storage: 10Gi
  storageClassName: YOUR_STORAGE_CLASS
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: hawk-dpkeys
  namespace: hawk
spec:
  accessModes:
    - ReadWriteOnce
  resources:
    requests:
      storage: 128Mi
  storageClassName: YOUR_STORAGE_CLASS
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: hawk-logs
  namespace: hawk
spec:
  accessModes:
    - ReadWriteOnce
  resources:
    requests:
      storage: 1Gi
  storageClassName: YOUR_STORAGE_CLASS
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: hawk-db
  namespace: hawk
spec:
  replicas: 1
  selector:
    matchLabels:
      app: hawk-db
  template:
    metadata:
      labels:
        app: hawk-db
    spec:
      containers:
        - name: mssql
          image: mcr.microsoft.com/mssql/server:2022-latest
          ports:
            - containerPort: 1433
          env:
            - name: ACCEPT_EULA
              value: "Y"
            - name: MSSQL_PID
              value: "Developer"
            - name: SA_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: hawk-secrets
                  key: SA_PASSWORD
          volumeMounts:
            - name: mssql-data
              mountPath: /var/opt/mssql
      volumes:
        - name: mssql-data
          persistentVolumeClaim:
            claimName: hawk-mssql-data
---
apiVersion: v1
kind: Service
metadata:
  name: hawk-db
  namespace: hawk
spec:
  selector:
    app: hawk-db
  ports:
    - name: mssql
      port: 1433
      targetPort: 1433
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: hawk-web
  namespace: hawk
spec:
  replicas: 1
  selector:
    matchLabels:
      app: hawk-web
  template:
    metadata:
      labels:
        app: hawk-web
    spec:
      containers:
        - name: web
          # Build/publish your own image and push it to a registry your TrueNAS cluster can pull from.
          # Example: ghcr.io/your-org/hawk-web:latest
          image: hawk-web:latest
          imagePullPolicy: IfNotPresent
          ports:
            - containerPort: 8080
          env:
            - name: ASPNETCORE_URLS
              value: "http://+:8080"
            - name: ASPNETCORE_ENVIRONMENT
              value: "Production"
            - name: ConnectionStrings__DefaultConnection
              value: "Server=hawk-db;Database=Hawk;User Id=sa;Password=$(SA_PASSWORD);TrustServerCertificate=true;Encrypt=false"
            - name: SA_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: hawk-secrets
                  key: SA_PASSWORD
            - name: Hawk__SeedAdmin__Email
              valueFrom:
                secretKeyRef:
                  name: hawk-secrets
                  key: SEED_ADMIN_EMAIL
            - name: Hawk__SeedAdmin__Password
              valueFrom:
                secretKeyRef:
                  name: hawk-secrets
                  key: SEED_ADMIN_PASSWORD
            - name: Hawk__Resend__BaseUrl
              valueFrom:
                configMapKeyRef:
                  name: hawk-config
                  key: RESEND_BASEURL
            - name: Hawk__Resend__ApiKey
              valueFrom:
                secretKeyRef:
                  name: hawk-secrets
                  key: RESEND_APIKEY
            - name: Hawk__Email__From
              valueFrom:
                configMapKeyRef:
                  name: hawk-config
                  key: EMAIL_FROM
            - name: Hawk__DisableHttpsRedirection
              valueFrom:
                configMapKeyRef:
                  name: hawk-config
                  key: Hawk__DisableHttpsRedirection
          volumeMounts:
            - name: dpkeys
              mountPath: /var/lib/hawk/dpkeys
            - name: logs
              mountPath: /app/logs
      volumes:
        - name: dpkeys
          persistentVolumeClaim:
            claimName: hawk-dpkeys
        - name: logs
          persistentVolumeClaim:
            claimName: hawk-logs
---
apiVersion: v1
kind: Service
metadata:
  name: hawk-web
  namespace: hawk
spec:
  selector:
    app: hawk-web
  ports:
    - name: http
      port: 8080
      targetPort: 8080
  type: ClusterIP
```

### 3) Expose The App

You have a few options:

- TrueNAS SCALE UI: create an Ingress (recommended) and terminate TLS there.
- Or, change the `hawk-web` Service `type` to `NodePort` and expose a port on the node.

Example Ingress (adjust host and ingress class for your cluster):

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: hawk
  namespace: hawk
spec:
  rules:
    - host: hawk.yourdomain
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: hawk-web
                port:
                  number: 8080
```

## Building And Publishing An Image (For TrueNAS)

TrueNAS pulls images from registries; it typically cannot build from your local workstation.

From this repo, you can build and push `Hawk.Web`:

```bash
docker build -t ghcr.io/YOUR_ORG/hawk-web:latest -f Hawk.Web/Dockerfile .
docker push ghcr.io/YOUR_ORG/hawk-web:latest
```

Then set `image: ghcr.io/YOUR_ORG/hawk-web:latest` in the YAML.

## Admin User Management

Admin users can manage other users from the Admin panel:

- **Admin** -> **Users**: list all users.
- **Create**: add a new user with email, password, and optional Admin role.
- **Reset Password**: reset a user's password.
- **Delete**: remove a user.

## Alert Policy

Each monitor has an `AlertAfterConsecutiveFailures` setting (1–20, default 1).

- `1`: alert on first failure after a success (default).
- `N > 1`: alert only after `N` consecutive failures, reducing noise from transient blips.

## Import From StatusCake

Hawk can import:

- StatusCake uptime tests (creates Hawk monitors)
- StatusCake uptime alerts (imports run history)

Import UI:

- In Hawk, go to `Admin` -> `Import StatusCake`.

### Export Tests From StatusCake (JSON)

Export the raw JSON from the StatusCake API and save it to a file:

```bash
export STATUSCAKE_API_TOKEN="..."
curl -sS "https://api.statuscake.com/v1/uptime?limit=100" \
  -H "Authorization: Bearer $STATUSCAKE_API_TOKEN" \
  > statuscake-uptime.json
```

Upload `statuscake-uptime.json` with import type `Tests`.

Notes:

- Only HTTP/HTTPS tests are imported.
- The StatusCake id is appended to the monitor name as `(sc:<id>)` so alerts can be mapped later.
- If a test uses `do_not_find` (inverted body match), Hawk imports it as disabled and emits a warning (Hawk v1 does not support inverted matches).

### Export Alerts From StatusCake (JSON)

The StatusCake alerts API is per-test. Export alerts for one or more tests:

```bash
export STATUSCAKE_API_TOKEN="..."
TEST_ID="123"
curl -sS "https://api.statuscake.com/v1/uptime/${TEST_ID}/alerts?limit=100" \
  -H "Authorization: Bearer $STATUSCAKE_API_TOKEN" \
  > "alerts-${TEST_ID}.json"
```

Hawk expects alerts in this combined format:

```json
[
  { "test_id": "123", "data": [ /* alerts */ ] },
  { "test_id": "124", "data": [ /* alerts */ ] }
]
```

You can build that file like this (requires `jq`):

```bash
jq -n --slurpfile a alerts-123.json '{test_id:"123", data: $a[0].data}' > alerts-123.wrapped.json
jq -n --slurpfile a alerts-124.json '{test_id:"124", data: $a[0].data}' > alerts-124.wrapped.json
jq -s '.' alerts-*.wrapped.json > statuscake-alerts.json
```

Upload `statuscake-alerts.json` with import type `Alerts`.

## Ando Build And Release

This repo includes an Ando build script: `build.csando`.

### Build And Test

```bash
ando run
```

### Publish (Artifacts + Container Image)

Publishes the app to `./artifacts/publish/Hawk.Web` and builds a local Docker image (`hawk-web:<version>`).
The image contains only the ASP.NET app. SQL Server is external and configured via `ConnectionStrings__DefaultConnection`.

```bash
ando run --dind -p publish
```

### Push To GHCR (Optional)

By default, the `publish` profile builds multi-arch (amd64 + arm64) and pushes to GHCR.

Override the destination with `GHCR_IMAGE=ghcr.io/<owner>/<name>`.
Ensure auth is available (recommended: `GITHUB_TOKEN` in CI, or `gh auth login` locally).

```bash
export GHCR_IMAGE=ghcr.io/YOUR_ORG/hawk-web
export GITHUB_TOKEN=...   # in GitHub Actions, this is provided automatically
ando run --dind -p publish
```

To build locally without pushing:

```bash
export HAWK_SKIP_GHCR=true
ando run --dind -p publish
```

### Versioning And CHANGELOG

- Project version is set to `0.9.2` in the `.csproj` files.
- The intent is to use `ando release` which automatically bumps versions from there.
- Changelog is tracked in `CHANGELOG.md`.
