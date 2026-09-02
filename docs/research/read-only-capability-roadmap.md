# Quorum read-only capability roadmap

Research date: 2026-09-01

## Executive recommendation

Do not turn the existing all-in-one JSON snapshot directly into one giant agent tool. First create typed, independently observable collectors; then build deterministic analyzers; finally expose small read-only tools over those analyzers.

```text
Discord GET endpoints / gateway cache
                |
        typed collectors
                |
   immutable normalized snapshots
                |
 deterministic analyzers + findings
                |
 narrowly scoped agent tools
```

The agent should explain stored evidence. It should not calculate security-critical permission results from raw JSON, receive a Discord client, select arbitrary HTTP routes, or possess a bot token.

## Important distinction: application safety vs credential safety

Quorum's `IServerConfigurationReader` has no mutation methods, which is a useful application boundary. Discord nevertheless requires mutation-capable permissions for several read endpoints:

| Read capability | Discord authority required | Authority also permits mutations? |
|---|---|---:|
| Audit log | `VIEW_AUDIT_LOG` | Limited |
| Invite inventory | `MANAGE_GUILD` or `VIEW_AUDIT_LOG`; full metadata requires `MANAGE_GUILD` | Yes for `MANAGE_GUILD` |
| Integrations | `MANAGE_GUILD` | Yes |
| AutoMod rules | `MANAGE_GUILD` | Yes |
| Guild widget settings and templates | `MANAGE_GUILD` | Yes |
| Webhook inventory | `MANAGE_WEBHOOKS` | Yes |
| Ban inventory | `BAN_MEMBERS` | Yes |
| Full member inventory | `GUILD_MEMBERS` privileged intent | Sensitive data access rather than a guild permission |
| Inactive-member prune estimate | `MANAGE_GUILD` and `KICK_MEMBERS` | Yes; not recommended for the initial collector |

Therefore, "our code only calls GET" is not a sufficient production threat model. For opt-in sensitive collectors:

1. Keep the Discord credential only in a collector process.
2. Allowlist exact GET routes or wrap them in typed methods.
3. Give the conversational runtime only normalized, redacted results.
4. Never expose webhook tokens, invite codes, bot tokens, or arbitrary request tools.
5. Audit every collector call with guild, requester, tool, result status, and correlation ID.
6. Consider separate basic and extended collector deployments so a server can choose its risk level.

## Collection tiers

### Tier 0 — coverage and collector self-audit

Build this first. Every other conclusion depends on it.

- Bot guild permissions and channel-specific effective permissions
- Enabled gateway intents and privileged-intent availability
- Which collectors were attempted, skipped, denied, rate-limited, partial, or successful
- Pagination completeness, cache freshness, capture time, API version, and schema version
- Quorum application-command visibility: default member permission, allowed channels, roles, and users when obtainable
- Snapshot redaction report and data-retention policy

Use explicit collection states:

```text
available | permission_denied | intent_disabled | unsupported
partial | rate_limited | transient_failure | stale | not_applicable
```

Never collapse these states into an empty array. `[]` means successfully observed and empty; `permission_denied` means unknown.

### Tier 1 — broad configuration with low additional sensitivity

These capabilities provide high value without full member or message collection:

- Full guild settings: verification, MFA, content filter, default notifications, locale, AFK/system/rules/public-update/safety-alert channels, system-channel flags, NSFW level, boost state, feature flags, and current incident data
- Invite-paused and raid-alert-disabled feature states
- Detailed role schema: raw permission integer, named permissions, hierarchy, managed-role tags, bot/integration ownership, colors, icons, flags, mentionability, and hoisting
- Detailed channel schema by type:
  - text/announcement: topic, NSFW, slowmode, default thread archive duration
  - voice/stage: bitrate, user limit, RTC region, video quality and status
  - forum/media: guidelines, tags, default reaction, default sort/layout, tag requirement, default thread slowmode
  - category: child membership and permission synchronization
- All explicit role and member overwrites, including orphaned target IDs
- Active threads visible to the bot and archived-thread inventory where permitted
- Scheduled events including recurrence, location/channel, creator, subscriber counts, and visibility
- Stage instances and voice-channel occupancy without presence tracking
- Emojis, stickers, and soundboard sounds, including availability, restrictions, managed ownership, and capacity pressure
- Onboarding, welcome screen, widget exposure, vanity URL, and guild templates where exposed

Useful new findings include:

- Quorum itself is over-privileged
- Quorum commands are usable outside the designated administration channel
- Server invites are paused or a raid/DM-spam incident is active
- Safety alerts are configured but disabled by guild feature state
- Public widget exposes an unexpected channel or instant invite
- Forum requires no tags, has no guidelines, or has inconsistent moderation defaults
- Slowmode differs unexpectedly across equivalent public channels
- Voice or stage channels have unexpected public connect/speak/request-to-speak access
- Managed bot/integration roles sit unusually high in the hierarchy
- Orphaned overwrite refers to a deleted role or member
- Archived private threads remain visible to unexpectedly broad roles

### Tier 2 — opt-in administrative inventories

These require stronger Discord permissions and collector isolation:

- Audit log, paginated and normalized by action type
- Invites and invite metadata
- Integrations and their application, scopes, sync, expiry, role, and revocation state
- Webhooks, creator/application ownership, target channel, type, and last-known baseline identity
- Complete AutoMod trigger metadata, enabled state, actions, destinations, exemptions, and creator
- Ban inventory and reasons
- Widget settings, vanity usage, and guild templates when `MANAGE_GUILD` is required

Redact or transform sensitive fields:

- Store an invite fingerprint instead of the usable invite code unless display is explicitly required.
- Never store or return webhook tokens.
- Treat ban reasons, audit actors, and user IDs as restricted administrative data.
- Make raw keyword filters optional because they may contain slurs, personal information, or internal evasion terms.

### Tier 3 — opt-in member intelligence

Full server permission and hygiene analysis requires the `GUILD_MEMBERS` privileged intent. Without it, Quorum can analyze roles and known direct overrides but cannot reliably answer how many members hold a role or enumerate every bot/member.

Possible capabilities:

- Role assignment counts and privileged-member inventory
- Effective permissions for a specific member in a specific channel
- Pending membership-screening state
- Join age, timeout state, unusual role combinations, and bot accounts
- Unused roles and direct overrides mapped to current members
- Privileged roles assigned to unexpectedly many accounts

Limitations:

- "Stale privileged account" cannot be proven from join date alone.
- Discord presence is not durable activity evidence.
- Reliable last-message activity requires message-history/content collection, which should remain a separate privacy-heavy product decision.
- The documented full member-list endpoint requires the privileged intent and pagination.

### Tier 4 — message-derived hygiene (defer)

Pinned-message inventory, channel activity, abandoned-channel detection, moderation workload, and content-policy analysis require `VIEW_CHANNEL`, often `READ_MESSAGE_HISTORY`, and sometimes Message Content access. This materially changes Quorum's privacy profile. Do not bundle it into configuration auditing by default.

## Deterministic analysis modules

### Effective permission engine

Implement Discord's documented order exactly:

1. `@everyone` guild permissions
2. union of member role permissions
3. owner and `ADMINISTRATOR` short-circuit
4. `@everyone` channel deny, then allow
5. combined role-overwrite deny, then combined allow
6. member overwrite deny, then allow
7. implicit permission effects such as lack of `VIEW_CHANNEL`, `SEND_MESSAGES`, or `CONNECT`
8. thread inheritance rules

Return a proof, not only a bitfield:

```json
{
  "permission": "ViewChannel",
  "result": "allowed",
  "steps": [
    { "source": "@everyone", "effect": "denied" },
    { "source": "role:Moderator", "effect": "allowed" }
  ],
  "complete": true
}
```

Support three subjects:

- a role;
- a hypothetical set of roles, which does not require member enumeration;
- a member, which requires that member's complete role assignment.

### Drift engine

Use normalized stable records and compare by Discord snowflake ID, not display name. Separate:

- create/delete;
- rename/reorder;
- permission gain/loss;
- visibility expansion/restriction;
- security-control enable/disable;
- collector coverage change.

A newly inaccessible collector is a visibility regression, not proof that Discord configuration changed. Correlate snapshot changes with audit events when possible, but retain snapshot evidence as the source of observed state. Discord retains audit log entries for 45 days.

### Sensitive-resource classifier

Combine explainable signals:

- administrator-selected labels stored only in Quorum;
- category membership;
- explicit name patterns;
- access restricted to privileged roles;
- presence of audit, report, incident, staff, or security workflows.

Name-based classification alone must never create a high-confidence finding.

### Bot and integration analyzer

Distinguish bot roles through managed role tags and integration/application metadata. Check:

- `ADMINISTRATOR` and other dangerous permissions;
- highest-role position and roles the bot could theoretically manage;
- access to sensitive categories;
- webhook ownership and posting destination;
- integration scopes, sync age, revocation state, and managed role;
- privilege increases between snapshots.

### Native-feature readiness

Make recommendations conditional on server features and use case. Useful checks include Community, Membership Screening feature presence, Onboarding, Welcome Screen, forum/media channels, announcement channels, scheduled events, AutoMod, safety alerts, widget exposure, invite pause state, and moderator MFA.

Discord no longer documents a complete Membership Screening configuration read API. Quorum can report feature presence and member `pending` state when member data is available, but should not claim it inspected the full screening form.

## Agent tool catalog

Expose resource retrieval separately from analysis. Suggested first contracts:

| Tool | Purpose | Typical inputs |
|---|---|---|
| `get_collector_coverage` | Explain known, unknown, partial, and stale evidence | `guild_id` |
| `get_guild_security_settings` | Return security-relevant guild and incident fields | `guild_id` |
| `list_roles` | Return typed role summaries and named sensitive permissions | `guild_id`, optional `risk_only` |
| `get_role_access` | Explain one role's guild/channel access | `guild_id`, `role_id`, optional `channel_id` |
| `list_channels` | Return typed channel configuration | `guild_id`, optional type/category |
| `explain_effective_permission` | Deterministic permission proof | subject, channel, permission |
| `compare_channel_to_category` | Explain synchronization and exposure differences | `channel_id` |
| `list_direct_member_overrides` | Inventory maintenance-heavy direct exceptions | optional `channel_id` |
| `get_automod_assessment` | Rules, coverage, destinations, and exemptions | `guild_id` |
| `get_invite_assessment` | Invite lifecycle findings without revealing codes | `guild_id` |
| `get_webhook_assessment` | Webhook ownership/destination risks without tokens | `guild_id` |
| `get_integration_assessment` | Integration and managed-role risks | `guild_id` |
| `list_audit_events` | Filtered administrative history | time range, action families, actor/target |
| `compare_snapshots` | Configuration drift | baseline/current or time range |
| `list_findings` | Deterministic findings, filtered and paginated | severity/category/status |
| `get_finding` | Full evidence and recommendation for one finding | finding ID |
| `get_native_feature_recommendations` | Contextual Discord-native opportunities | `guild_id` |
| `get_quorum_access_review` | Review Quorum permissions and command placement | `guild_id` |

Tool rules:

- Guild ID comes from the authenticated interaction context, not free-form model input.
- Tools authorize the invoking administrator before accessing evidence.
- Use IDs internally and resolve display names only for presentation.
- Paginate and cap results; return continuation tokens rather than huge payloads.
- Return freshness, coverage state, and evidence references on every call.
- Analysis tools call deterministic code; the model only summarizes and contextualizes.
- Do not expose `get_raw_snapshot`, arbitrary SQL, arbitrary REST, file access, or mutation-shaped parameters.

## Finding and evidence model

Extend the proposed model to preserve uncertainty:

```yaml
id: QPERM-004
rule_version: 1
guild_id: "..."
category: permissions
severity: critical
status: fail        # pass | fail | review | unknown | not_applicable
confidence: 1.0
title: Sensitive child channel is less restrictive than its category
observed_at: 2026-09-01T18:00:00Z
evidence:
  - collector: channels
    snapshot_id: "..."
    resource_type: channel
    resource_id: "..."
    field: permission_overwrites
    completeness: complete
risk: "..."
recommendation: "..."
read_only: true
```

Confidence should describe evidence completeness, not how severe the issue feels. Posture and coverage must be separate scores. Unknown checks should neither pass nor silently lower a known-only score; reports should show both, for example `Known posture: 82/100; assessment coverage: 61%`.

## Recommended implementation order

### Milestone 1 — trustworthy typed snapshot

1. Replace anonymous `object` fields with versioned records.
2. Add collector status, required permission, freshness, paging, and redaction metadata.
3. Capture Quorum's own guild/channel permissions.
4. Expand guild, role, channel, forum, thread, incident, event, and onboarding fields.
5. Persist immutable snapshots with retention controls.

### Milestone 2 — permission and drift core

1. Implement and fixture-test effective permission calculation.
2. Implement category synchronization and direct-overwrite analysis.
3. Normalize snapshots and implement ID-based drift.
4. Add versioned findings and evidence references.

### Milestone 3 — agent-ready low-risk tools

Start with coverage, guild security settings, roles, channels, permission explanation, category drift, findings, and Quorum self-review. These provide useful conversation without adding privileged member/message access.

### Milestone 4 — isolated extended collectors

Add audit log first, then optional AutoMod/invites/integrations, then webhooks and bans only when administrators accept the credential implications. Add full members separately and require an explicit privacy/retention decision.

### Milestone 5 — reporting and baselines

Add administrator-owned baseline annotations, scheduled captures, changes-since reports, fleet-level summaries, and alerting. Quorum annotations remain in Quorum and never modify Discord.

## Current implementation gaps

The current `ServerConfigurationSnapshot` is a useful spike, not an agent-ready contract:

- most sections are anonymous `object` values with no durable schema;
- cached sections do not carry availability or freshness metadata;
- AutoMod omits enabled state, trigger metadata, exemptions, and action metadata;
- channels omit most type-specific settings and parent/category linkage;
- roles omit managed ownership/tags and named permission explanations;
- events omit recurrence, location/channel, creator, and subscriber counts;
- collectors do not prove pagination completeness;
- only REST failures become unavailable sections;
- there is no persistence, drift engine, permission proof, finding engine, redaction contract, or agent runtime;
- the bot's own effective authority is not reported.

These should be corrected before exposing the snapshot to a language model.

## Primary Discord references

- [Guild resource](https://docs.discord.com/developers/resources/guild)
- [Channel resource](https://docs.discord.com/developers/resources/channel)
- [Permissions](https://docs.discord.com/developers/topics/permissions)
- [Gateway intents](https://docs.discord.com/developers/events/gateway)
- [Audit log](https://docs.discord.com/developers/resources/audit-log)
- [Auto Moderation](https://docs.discord.com/developers/resources/auto-moderation)
- [Webhooks](https://docs.discord.com/developers/resources/webhook)
- [Application commands and command permissions](https://docs.discord.com/developers/interactions/application-commands)
- [Guild scheduled events](https://docs.discord.com/developers/resources/guild-scheduled-event)
- [Guild templates](https://docs.discord.com/developers/resources/guild-template)
- [Soundboard](https://docs.discord.com/developers/resources/soundboard)
