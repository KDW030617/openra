# LLM Arena

Pit two Ollama Cloud models (GLM vs DeepSeek by default) against each other in an OpenRA Red Alert skirmish.
Each model controls its player exclusively through MCP tools.

```
 OpenRA (host)                                   llm-arena (Python)
 ┌──────────────────────────────┐   TCP JSON    ┌──────────────┐ stdio MCP ┌──────────────┐  HTTPS  ┌─────────────┐
 │ GLM bot ── LlmBridgeBotModule│◄────:7701────►│ mcp_server.py│◄─────────►│ Commander GLM│◄───────►│ Ollama Cloud│
 │ DeepSeek ─ LlmBridgeBotModule│◄────:7702────►│ mcp_server.py│◄─────────►│ Commander DS │◄───────►│  (models)   │
 └──────────────────────────────┘               └──────────────┘           └──────────────┘         └─────────────┘
```

- `LlmBridgeBotModule` (C#, `OpenRA.Mods.Common/Traits/BotModules/Llm/`) is a regular bot module. It listens on
  `127.0.0.1:<Port>`, answers state queries and turns commands into ordinary player orders on the game thread.
  Finished structures are placed automatically, so models only decide *what* to build and roughly *where*.
- `mcp_server.py` exposes one player as MCP tools: `get_state`, `get_game_info`, `build`, `cancel_build`, `move`,
  `attack_move`, `attack`, `stop`, `deploy`, `sell`.
- `arena.py` starts one MCP server per player, connects to it as an MCP client, and runs an Ollama tool-calling
  loop per model (`agent.py`). Every turn the model gets a fresh state snapshot plus its own plan from the
  previous turn.
- The system prompt (`prompts.py`) explains Red Alert basics (economy, power, production, combat) and embeds a
  briefing fetched once per game with `get_game_info`: your spawn, possible enemy spawns, ore fields, a coarse
  terrain grid, and the faction's full tech tree with costs, requirements and unit roles. The briefing only
  contains what a human sees in the lobby map preview and the build palette, never hidden enemy information.

## Setup

Windows quick setup (run from the repository root):

```powershell
.\llm-arena\setup.cmd
```

The script creates or reuses the Python environment, installs dependencies, and checks both MCP servers.
Create a key at https://ollama.com/settings/keys and paste it into the hidden prompt in your terminal.
The key is encrypted with Windows DPAPI in the ignored `.local/ollama-key.xml` file. Only the same Windows
account on the same computer can decrypt it. `run.ps1` loads it for the Python process and restores the
calling shell's previous environment afterward. Do not paste your key into chat or source files.

The `.cmd` entry points use a child PowerShell process with an execution policy override, so they also
work when this machine blocks `.ps1` files. They do not change the machine or user execution policy.

No local Ollama installation, GPU, or model download is required for direct Cloud access. These agents use
Ollama Cloud independently of the model powering your Codex conversation. See the
[Ollama Cloud documentation](https://docs.ollama.com/cloud).

```powershell
# Prepare dependencies without entering a key yet:
.\llm-arena\setup.cmd -SkipApiKey
# Replace a saved key:
.\llm-arena\setup.cmd -ReplaceApiKey
# Check authentication and model tool calling without starting a game:
.\llm-arena\run.cmd -CheckCloud
# Check MCP without a key or network requests:
.\llm-arena\run.cmd -CheckMcp
# Show available model names:
.\llm-arena\run.cmd -ListModels
```

Cloud checks send two short inference requests per distinct selected model, consuming Ollama usage.
They verify authentication, a tool call, and the follow-up tool result. The same checks run before each
match, so bad keys and unavailable models fail before waiting for the game. A model list alone does not
verify authenticated inference. When using an existing `OLLAMA_API_KEY` environment variable, the terminal runner
prefers the process value, then the encrypted file, then the Windows user environment variable.

For other operating systems, create a virtual environment with Python 3.11 or later, install
`requirements.txt`, set `OLLAMA_API_KEY` in your shell, and run `python arena.py` from this directory.

## Running a match

### Terminal workflow

1. Build and start the game (`make.ps1 all`, then `launch-game.cmd`) with the Red Alert mod.
2. Create a skirmish. Add the bots **GLM (LLM)** and **DeepSeek (LLM)** to two slots on opposing teams and move
   yourself to a spectator slot.
3. Start the game. The in-game chat shows `LLM bridge for ... listening on 127.0.0.1:7701/7702`.
4. Run the arena (it waits for the game if started earlier):

   ```powershell
   # From the repository root:
   .\llm-arena\run.cmd
   .\llm-arena\run.cmd -GlmModel glm-5.3 -DeepSeekModel deepseek-v4.1-flash -Interval 3
   ```

Transcripts are written to `logs/<timestamp>/<agent>.jsonl`.

| Option | Default | Notes |
| --- | --- | --- |
| `--glm-model` / `ARENA_GLM_MODEL` | `glm-5.3` | Any model name from `--list-models` |
| `--deepseek-model` / `ARENA_DEEPSEEK_MODEL` | `deepseek-v4-pro:0813` | |
| `--host` / `OLLAMA_HOST_URL` | `https://ollama.com` | `http://localhost:11434` uses a signed-in local Ollama (`<model>:cloud` names) |
| `--interval` | `5` | Seconds between turns |
| `--max-tool-rounds` | `8` | LLM/tool round trips per turn |
| `--check-cloud` | off | Check MCP and authenticated tool inference, then exit |
| `--check-mcp` | off | Check both MCP servers without a key or game |
| `--watch-games` | off | Wait for subsequent matches until stopped |

API model names must match `--list-models`. Direct Cloud names such as `glm-5.3` differ from the
`:cloud` names used by the signed-in local Ollama app.

## Verification

Run the offline tests from the repository root. They cover authentication failures, quota and network
errors, missing models, tool inference checks, and a commander using the real MCP subprocess with a
simulated game bridge and simulated Ollama responses. Match restart tests cover briefing and plan resets, stale Cloud
responses, state timeouts and socket reconnection without duplicate orders. They do not require a key
or consume Cloud usage.

```powershell
.\llm-arena\.venv\Scripts\python.exe -m unittest discover -s llm-arena/tests -v
```

## Using the MCP server from other clients

Any MCP client can drive a player while a match runs, e.g. Claude Code (`.mcp.json`):

```json
{
  "mcpServers": {
    "openra-glm": {
      "command": "llm-arena/.venv/Scripts/python",
      "args": ["llm-arena/mcp_server.py", "--port", "7701"]
    }
  }
}
```

## Notes and limitations

- Bridge ports and bot types are defined in `mods/ra/rules/llm-ai.yaml`; currently only the Red Alert mod
  registers the LLM bots. Add more `ModularBot` / `LlmBridgeBotModule` pairs there for other models.
- The bridge only listens on loopback and only exists while a game with an LLM bot is running on the host.
- Requests are processed on bot ticks, so they time out while the game is paused.
- Expired bridge requests are discarded before execution, rather than applied later when the game resumes.
- `get_state` and `get_game_info` include an optional additive `game_id`. The MCP bridge sends it back with
  commands to reject orders from a previous match; older bridge clients may omit it.
- Like every OpenRA bot, LLM bots run only on the host and are disabled in replays. Their orders are recorded,
  so replays of LLM matches play back normally.
