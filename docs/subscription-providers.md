# Qwen, Kimi, Perplexity and DeepSeek

ClaudeRevit can run its in-Revit chat through the official **Qwen Code** and
**Kimi Code** subscription CLIs. They connect to the same local Revit MCP tools as
Claude Code and Codex. Provider/model choices and CLI conversations are independent.

## Availability

| Provider | Subscription integration in ClaudeRevit | Authentication and billing |
|---|---|---|
| Qwen Code | Implemented, via ACP | **Qwen Account with Subscription**, Plus/Pro at chat.qwen.ai; explicit `qwen-subscription` route |
| Kimi Code | Implemented, via ACP | Official Kimi Code membership login in a separate ClaudeRevit CLI profile |
| Perplexity | Not available as a subscription adapter | Pro/Max and the API Platform have separate billing. An API key does not turn the API into subscription usage. |
| DeepSeek | No confirmed official subscription adapter | The documented integration uses an API key and token billing. No supported consumer-subscription OAuth/CLI route was identified. |

Perplexity and DeepSeek availability is explained in **Settings → Subscription
(MCP)**. They are not presented as working subscription agents. Existing alternative
API settings remain separate. Qwen/Kimi errors never fall back to an API provider.

The availability review used official documentation on 2026-11-17:

- [Qwen account authentication](https://github.com/QwenLM/qwen-code/blob/main/docs/users/configuration/auth.md)
- [Current Kimi Code CLI](https://github.com/MoonshotAI/kimi-code)
- [Kimi Code membership FAQ](https://www.kimi.com/code/docs/en/kimi-code/faq.html)
- [Perplexity subscription plans and API Platform](https://www.perplexity.ai/help-center/en/articles/11187416-which-perplexity-subscription-plan-is-right-for-you)
- [DeepSeek API billing](https://api-docs.deepseek.com/quick_start/pricing/)

## Setup

Install current official CLIs using a supported Node.js version:

```powershell
npm install -g @qwen-code/qwen-code
npm install -g @moonshot-ai/kimi-code
```

Use the current **Kimi Code** package, not the legacy Python `kimi-cli`. The npm
Kimi package requires Node.js 22.19 or newer; a current Node.js LTS installation is
recommended. The official native Kimi Code executable is also accepted when it
implements the same ACP interface. On Windows, follow Kimi's installation
requirements, including Git for Windows.

1. Open **Settings → Subscription (MCP)** in ClaudeRevit. Keep `qwen` and `kimi`, or
   enter their full executable paths if Revit cannot see the updated PATH.
2. Click **Sign in** for the chosen provider. For Qwen, select **Qwen Account with
   Subscription** in `/auth`. For Kimi, complete the membership login in the window
   opened by ClaudeRevit.
3. Click **Check subscription**. This reads authenticated CLI metadata without
   requesting a model answer or connecting to the Revit tool server.
4. Save settings. No Anthropic or alternative-provider API key is required.
5. In the chat agent picker, choose **Qwen Code · subscription** or **Kimi Code ·
   subscription**, refresh models, then select a model. The list comes from the
   installed CLI; it is not a hardcoded list of model names. Reasoning choices are
   shown only when the CLI advertises them.

Qwen uses its official account store, with `qwen-subscription` explicitly selected
on every managed launch. Legacy `qwen-oauth` and API-key routes are not accepted as
proof of this subscription route.

Kimi uses `%APPDATA%\ClaudeRevit\providers\kimi` as `KIMI_CODE_HOME`. Sign in from
ClaudeRevit even if another Kimi profile is already logged in. The CLI owns the
OAuth credentials; ClaudeRevit does not copy them into its settings or chat files.
This profile is shared across documents, while conversations use separate working
directories and session IDs.

## Behaviour and safeguards

- Each provider has its own executable, model and reasoning preferences. The old
  shared Claude/Codex model override is never applied to Qwen or Kimi.
- Each document and each concurrent Revit instance has separate CLI session files.
  Follow-up messages restore the matching provider's session. Clearing the chat
  clears these identities; switching models does not discard the conversation.
- Before every prompt, including after session restoration, Qwen must advertise
  the selected model's exact `qwen-subscription` authentication metadata. Kimi must
  identify itself as the current Kimi Code CLI and expose a `kimi-code/` model.
  Missing, conflicting or unavailable subscription routes stop the request.
- The CLI receives only the loopback MCP endpoint for the active turn/document.
  Only exact advertised ClaudeRevit tool names can receive an ACP one-call
  permission. Client filesystem/terminal RPCs and unrelated tools are denied.
  Qwen built-in tools are explicitly excluded; Kimi uses a managed tool allowlist.
- Stop cancels the CLI, closes that turn's MCP endpoint and waits for an already
  running Revit operation to settle. A late request cannot enter another turn.
- Neither adapter retries a prompt after an uncertain result. Kimi itself can
  repeat an MCP call after a transport failure, so these channels require a unique
  `operation_id` in each outer tool arguments object. The server retains the
  result, failure or in-flight operation for the turn and returns it on reconnect.
  Reusing an ID with changed arguments is rejected. Closed channels are rejected,
  including after restart. This is a turn-scoped receipt, not a cross-restart
  transaction journal; after an uncertain edit, inspect the model before sending
  a new operation.
- Attachments remain scoped to the conversation and are available through Revit's
  attachment tool. Direct image input is sent only when ACP advertises image support.
- ACP context occupancy is not billable token usage. The UI reports the provider,
  model, tool activity and elapsed time; it does not invent token costs.

Qwen/Kimi are added to the chat and `plugin_settings` agent preferences. The
existing benchmark modeller/judge picker remains limited to its established
Claude/Codex/API backends.

## Troubleshooting

| Message or symptom | Action |
|---|---|
| CLI not found | Restart Revit after installing the CLI, or select its full path in Settings. npm `.cmd` launchers are resolved to the installed Node entry point without running a shell command. |
| Subscription route not confirmed | Complete the correct account login, refresh the model list and choose a confirmed subscription model. An existing API key or legacy Qwen login is insufficient. |
| Kimi identity or ACP version unsupported | Update the current official Kimi Code package and check the executable path. The Python CLI has a different protocol. |
| Saved model unavailable | Refresh the list and select an available subscription model. No substitute provider is used. |
| MCP server cannot start | Check the local port in Settings. The provider is not launched if the local server failed to bind. |
| Quota/authentication/connection error | Resolve the account or connection issue, inspect the latest Revit changes, then send a new request deliberately. No automatic model-request retry is performed. |

## Validation

`ClaudeRevit.SubscriptionTests` uses isolated executable fixtures without accounts
or model requests. It checks subscription metadata and routing, modern and legacy
ACP model selection, permissions, session restoration, streaming, attachments,
transport termination, cancellation, bounded output and Windows npm launch paths.

`ClaudeRevit.McpTests` runs the real HTTP server with an isolated dispatcher. It
checks required operation IDs, concurrent duplicate calls across separate MCP
connections, mismatched ID reuse, gateway argument handling and late-call rejection.
Unit tests cover receipt failures, result isolation and per-document session paths.
`ClaudeRevit.PaneTests` verifies provider/model choices and stale asynchronous
catalog results on Windows. These suites run in CI and before release packaging.

A real-CLI smoke check on 2026-11-17 used Qwen Code **0.28.0** and Kimi Code
**2.6.3**, running on Node.js **24.22.1** with isolated temporary profiles. Both
accepted the production launch arguments and ACP `initialize` (protocol 1),
advertised HTTP MCP, session restoration and images, and exited cleanly after EOF.
Only version/initialization checks were made; no login, session or prompt request
was sent. This also verified Kimi's expected `Kimi Code CLI` identity. Qwen's
generic `initialize.authMethods` is not used to prove subscription billing.

These checks do not establish a successful login to a user's paid account, account
quota availability or successful native modelling inside Revit. Those require the
installed Windows application and the user's own subscription.

## Краткая настройка на русском

Установите официальные `@qwen-code/qwen-code` и `@moonshot-ai/kimi-code`, откройте
**Настройки → Подписка (MCP)**, укажите пути при необходимости и нажмите **Войти**.
В Qwen выберите **Qwen Account with Subscription**. В Kimi войдите из окна,
открытого плагином: для него используется отдельный профиль. Нажмите **Проверить
подписку**, сохраните настройки и выберите нужного агента в чате. Обновите список
моделей; API-ключ для этих двух режимов не требуется.

Perplexity Pro/Max оплачивается отдельно от API; подтверждённого официального
подписочного подключения DeepSeek для этой интеграции нет. Эти два провайдера не
выдаются за подключённые по подписке. При ошибках Qwen/Kimi перехода на платный API
не происходит.
