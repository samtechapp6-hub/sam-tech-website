# SAM Tech Support API

An independent support-ticket API for the SAM Tech website. Ticket records are stored in a dedicated SQL Server database and are separate from SAM DENTAL clinical records.

## Configuration

Create an empty SQL Server database named `SamTechSupport` (or choose another database name), then configure these environment variables on the API host:

- `SUPPORT_DATABASE`: SQL Server connection string for the dedicated support database.
- `SUPPORT_ADMIN_EMAIL`: initial support administrator email.
- `SUPPORT_ADMIN_PASSWORD`: initial administrator password (at least 10 characters). Keep it in the host's secret store; do not commit it.
- `SUPPORT_ADMIN_NAME`: optional display name for the administrator.
- `SAM_DENTAL_INTEGRATION_KEY`: a random secret of at least 32 characters shared with the SAM DENTAL API host. Keep it in the host secret store; do not commit it.

On startup, the API applies the idempotent schema in `Database/Schema.sql`. The SQL login therefore needs permission to create the listed tables and indexes during initial setup. Customer accounts remain pending until a support administrator reviews and activates them.

## Hosting with the website

The marketing website serves `support.html` and its JavaScript from the SAM Tech site. Route `/api/*` on that same HTTPS host to this API with the web server or reverse proxy. Keeping the portal and API same-origin lets the API use an HTTP-only, SameSite session cookie without exposing the session token to page scripts.

Use HTTPS for any public deployment. Do not expose this service over plain HTTP. Configure SQL Server so it is reachable only from the API host, and back up the support database independently.

## Initial scope

- Customer account request and administrator approval.
- Customer login and per-organization ticket isolation.
- Ticket creation, replies, status updates, and support-admin inbox.
- Current product catalog contains SAM DENTAL; more SAM Tech products can be added later.
- SAM DENTAL creates customer tickets through `POST /api/integrations/sam-dental/tickets`; each request is authenticated with `X-SamTech-Integration-Key` and associated with the active support account email configured on that SAM DENTAL installation.
- Attachments, email/SMS notifications, self-service password recovery, and automated SLA handling are not included yet.
