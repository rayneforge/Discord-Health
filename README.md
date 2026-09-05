# Quorum

**Quorum** is a conversational Discord administration and security agent. It can inspect the server in which it is invoked, explain what it can and cannot see, reason over deterministic security findings, and use narrowly scoped administration tools.

Authorized read tools execute immediately. Write-shaped tools do not modify Discord: they create a durable approval card in the invoking chat. Only an administrator clicking Approve can pass the proposal through requester reauthorization, precondition validation, execution, and post-write verification.

## Commands

- `/quorum message:<request>` talks to the agent. The response is private to the requester.
- `/server-config` captures a fresh snapshot and returns the full review as JSON.
- `/quorum-findings` returns a compact deterministic finding summary.

Quorum registers commands globally by default and works in every server where it is invited. `Discord:GuildId` is only an optional development shortcut for faster command registration in one test server.

## Agent tools

Every invocation creates a tool catalog bound to the Discord interaction's server ID and requester ID. Those values are not model arguments, so the model cannot redirect a tool to another server or impersonate another requester.

Implemented read tools:

- fresh configuration scan, immutable snapshot persistence, and drift comparison;
- targeted server-scoped lookup of roles, channels, and categories by name, returning exact IDs without loading every configuration section;
- segmented inspection of the guild overview, roles, channels and overwrites, emojis, stickers, scheduled events, voice state, bans, invites, integrations, webhooks, AutoMod, audit log, onboarding, welcome screen, and collector coverage;
- deterministic security findings with severity/status filters;
- effective role-permission explanation for a channel;
- a capability/gap report.

Implemented write-shaped tools include:

- channels/categories: create, rename, topic, slowmode, delete, role permission overwrites;
- roles: create, replace permissions, delete, assign to or remove from members;
- moderation: timeout, kick, ban, and unban;
- operations: lock/archive threads, delete webhooks, revoke invites, and create scheduled events;
- security/community: create/enable/disable/delete AutoMod keyword rules, update welcome-screen state/description, and update onboarding state/mode.

Each tool creates the same generic typed proposal. Unsupported variants are reported as gaps and are never substituted with a different mutation.

Role-targeting tools accept either a server-scoped exact role name or an ID. Permission-set tools accept canonical Discord permission names as well as raw bitsets. New text-channel proposals can target a category by exact name, including a category proposed in the same overhaul; approve and execute the category card before approving its dependent channel cards.

## Permission behavior

Quorum gracefully handles lesser permissions. Each collector reports its own status, reason, and required Discord permission. A denied collector does not invalidate the rest of the snapshot, and the agent is instructed to distinguish observed facts from permission gaps and unsupported API surfaces.

Common read permissions include View Audit Log, Manage Server, Manage Webhooks, and Ban Members. You may grant elevated permissions if desired; Quorum still reports the effective visibility rather than assuming complete access. Quorum uses the `Guilds` and `Guild Voice States` gateway intents and does not request message content or the full member list.

## Create and invite the Discord bot

1. In the [Discord Developer Portal](https://discord.com/developers/applications), create an application named **Quorum**.
2. On **Bot**, create the bot and copy/reset its token. Treat the token like a password.
3. On **OAuth2 → URL Generator**, select `bot` and `applications.commands`.
4. Choose one of these installation profiles, open the link, and select a server:

   - [Current-tool least privilege](https://discord.com/oauth2/authorize?client_id=1544520777372536855&permissions=18993150807222&integration_type=0&scope=bot%20applications.commands) requests the permissions used by Quorum's current collectors, approval messages, and approved actions. It preserves Discord channel-overwrite restrictions.
   - [Administrator](https://discord.com/oauth2/authorize?client_id=1544520777372536855&permissions=8&integration_type=0&scope=bot%20applications.commands) is convenient for development and maximum collector coverage, but it grants every Discord permission and bypasses channel overwrites.

The least-privilege profile currently includes View Channel, Send Messages, Send Messages in Threads, Embed Links, Attach Files, Read Message History, View Audit Log, Manage Server, Manage Channels, Manage Roles, Manage Webhooks, Kick Members, Ban Members, Moderate Members, Create Events, Manage Events, and Manage Threads. Recalculate the bitfield when new tool families are added.

Discord role hierarchy still applies: place Quorum's highest role above every role or member it needs to edit, assign, kick, ban, or timeout. Administrator does not bypass role hierarchy.

You do not need to put a server ID in Quorum's configuration for normal operation.

`/quorum` is available to server members who can view the invoking channel, but each tool independently checks the requester's current Discord permissions. For example, channel changes require Manage Channels, role changes require Manage Roles, bans require Ban Members, and sensitive reads require their corresponding visibility permission. Discord channel permissions may restrict the command further. Approval buttons are valid only in the same channel where that proposal was requested, and the clicking user must still be an administrator.

### Server isolation

Quorum derives the server ID, requester ID, and approval-channel ID from the authenticated Discord interaction. A fresh tool catalog closes over those values for that invocation; they are not tool arguments and are never supplied by the model. The model may select a resource ID such as a channel, role, thread, rule, webhook, or member, but the implementation resolves that resource through the invocation's captured server. An ID belonging to another server is therefore unavailable and fails closed instead of redirecting the operation.

`Discord:GuildId` does not authorize or select an execution target. It only changes slash-command registration from global registration to one development server; runtime tools still use `Context.Guild.Id` from each interaction.

## Configure

The Discord token is compatible with .NET User Secrets:

```powershell
cd "E:\Projects\Discord Health"
dotnet user-secrets set "Discord:Token" "YOUR_BOT_TOKEN" --project "src\DiscordHealth.Runtime"
```

The conversational model is provider-configured. The first implemented provider is OpenAI using the Responses API directly:

```powershell
dotnet user-secrets set "Agent:Enabled" "true" --project "src\DiscordHealth.Runtime"
dotnet user-secrets set "Agent:Provider" "OpenAI" --project "src\DiscordHealth.Runtime"
dotnet user-secrets set "Agent:ApiKey" "YOUR_OPENAI_API_KEY" --project "src\DiscordHealth.Runtime"
dotnet user-secrets set "Agent:Model" "gpt-4o-mini" --project "src\DiscordHealth.Runtime"
```

`Agent:Endpoint` defaults to `https://api.openai.com/v1`; override it only for an OpenAI-compatible endpoint. Provider B/C can be added behind the same configuration boundary without changing Discord tools. Disabling the agent does not disable the deterministic snapshot commands.

Optional development-only guild registration:

```powershell
dotnet user-secrets set "Discord:GuildId" "TEST_SERVER_ID" --project "src\DiscordHealth.Runtime"
# Return to global registration:
dotnet user-secrets remove "Discord:GuildId" --project "src\DiscordHealth.Runtime"
```

## Run locally

```powershell
dotnet run --project "src\DiscordHealth.Runtime"
```

If Windows Smart App Control blocks unsigned Discord.Net assemblies, use Docker instead of weakening the host security policy.

## Run with Docker

```powershell
cd "E:\Projects\Discord Health"
$env:DISCORD_TOKEN = "YOUR_BOT_TOKEN"
$env:QUORUM_AGENT_ENABLED = "true"
$env:QUORUM_AGENT_PROVIDER = "OpenAI"
$env:QUORUM_AGENT_API_KEY = "YOUR_OPENAI_API_KEY"
$env:QUORUM_AGENT_MODEL = "gpt-4o-mini"
docker compose up --build -d
docker compose logs -f quorum
```

`DISCORD_GUILD_ID` is optional.

To enable approval-gated writes:

```powershell
$env:QUORUM_WRITES_ENABLED = "true"
docker compose up -d
```

Approval cards appear in the invoking chat. Approvers must be administrators. Proposals expire, reject duplicate approval, optionally prevent self-approval, compare the current value with the proposed precondition, execute only the typed action, and verify the resulting Discord value.

Authorization is permission-trimmed rather than all-or-nothing. Before creating a proposal, Quorum verifies both the requester's permission and the bot's permission for that exact action. It also applies Discord-style role hierarchy checks and prevents requesters from granting permissions they do not possess. After approval, all requester permissions and hierarchy constraints are evaluated again; a revoked permission makes the proposal fail closed without executing.

Quorum also enforces a non-approvable self-protection boundary. It refuses to propose or execute role-permission changes, role membership changes, channel overwrites, or moderation actions targeting its own bot account, its assigned roles, or `@everyone`. These attempts are security-warning events in the runtime log. Change Quorum's own access directly in Discord instead.

Stop the container:

```powershell
docker compose down
Remove-Item Env:DISCORD_TOKEN -ErrorAction SilentlyContinue
```

The image runs as a non-root user with a read-only root filesystem, dropped Linux capabilities, and a persistent `/data` volume for snapshots and proposals.

## Agent and tool logs

```powershell
docker compose logs -f quorum
```

Agent invocations log guild/channel/requester scope, provider, model, tool count, request/response sizes, and duration. Every tool logs its name, duration, result size, and success or categorized failure. Prompts, tool result bodies, Discord tokens, and OpenAI API keys are not logged.

## Verify

```powershell
dotnet build "DiscordHealth.sln"
dotnet test "DiscordHealth.sln"
docker build --target test --tag quorum-tests:local .
```

See [Architecture](docs/architecture.md), [read visibility](docs/read-only-scope.md), and the [capability roadmap](docs/research/read-only-capability-roadmap.md).
