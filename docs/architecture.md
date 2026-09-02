# Architecture

Quorum follows the reference projects' agent/tool separation while binding every capability to the current Discord interaction.

```text
Discord interaction
  -> Quorum conversational agent
     -> request-scoped tool catalog (guild ID + requester ID)
        -> typed read services
        -> typed write proposal services
           -> durable pending approval card in the invoking chat
              -> administrator Discord component approval
                 -> validate -> compare -> execute -> verify
```

The model never receives a generic Discord client, arbitrary HTTP client, or a tool argument that selects the guild or requester. Discord.Net remains inside `DiscordAdapter`. Read tools return typed snapshots and explicit collector gaps. Write-shaped tools may create proposals, but only the approval handler can call an approved executor.

The conversational runtime follows GoodyAI's Microsoft Agent Framework pattern: a provider-configured client creates a named `ChatClientAgent` and supplies `AIFunction` tools. OpenAI is the first provider and uses the Responses API directly. It also preserves a bounded per-guild/per-channel/per-requester conversation history.

## Safety invariants

- Read operations may run immediately.
- A write-shaped agent function never directly changes Discord.
- Guild and requester scope come from the Discord interaction, not model input.
- Unsupported mutations are reported as gaps, never simulated through a different tool.
- Every approved mutation has a typed precondition, required permission, risk level, expiration, durable approval record, and post-write verification.
- Deferred Discord interactions always receive a completion or explicit error when still valid.
