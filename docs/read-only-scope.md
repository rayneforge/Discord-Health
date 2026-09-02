# Read-only configuration scope

This document defines the configuration visibility available to Quorum's commands and future conversational agent.

The snapshot includes cached guild metadata, channels and permission overwrites, roles and permissions, emojis, stickers, voice states, and scheduled events. It also attempts REST-backed sections for bans, invites, integrations, webhooks, and AutoMod rules.

Discord visibility depends on the bot's permissions and Discord API support. Each REST-backed section is independently captured as either `available` with data or `unavailable` with a reason. One denied section does not fail the snapshot.

The initial bot does not request privileged gateway intents and does not inspect message content or member lists. A configuration inventory does not require those data sources.
