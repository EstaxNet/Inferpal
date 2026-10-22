# Running Inferpal's ACP agent across machines

The [ACP agent](acp.md) is meant to run on the machine of the editor that starts it: the editor launches
`Inferpal.Host --acp` and talks to it over its standard input and output. This page is for the other case — a client
on another computer, a phone, a browser — and it starts with the one fact that decides everything else.

## Whoever holds the connection drives the machine

The program connected to the agent's input and output is, for Inferpal, **the user**:

- **It answers the approvals.** The changes, commands and web requests the model makes ask first — and the question
  goes to that program, which can say yes. The approval card is a real safeguard when you are the one reading it; it
  is none against a client that is not you.
- **Some things never ask.** Reading tools (`read_file`, `list_files`, `search_in_files`, code search) work in the
  folder the client names when it opens a session — any folder of the machine, your home folder or a drive's root
  included. Building and testing that folder (`run_tests`, `get_diagnostics`) runs the project's own code without
  asking. The slash commands it sends act as yours do: `/commit-exec` runs `git commit` with the repository's hooks,
  `/import` writes your settings.
- **It reads your conversations.** It can list, reopen and delete every conversation saved on the machine, those of
  Visual Studio and VS Code included.
- **It can start programs and reach addresses.** The MCP servers a client sends when it opens or reopens a session
  are started — programs, or connections to any address it names — as the ones you configure in your own editor are,
  even with Inferpal's own MCP servers turned off.
- **Your standing permissions apply to it**: the `allow` rules of your machine's settings, the "Always" grants given
  during a session, and — with `securityAlertsDisabled` on — no approval at all. What it changes stays: your files,
  your settings, the `.inferpal/` instructions your own sessions read later.

Inferpal does not try to tell a trusted client from another one: nothing in the protocol carries a secret it could
check, and a check that only looked like one would be worse than none. **What protects the machine is who can reach
the connection.**

## On one machine: nothing to do

This is how every client described in [acp.md](acp.md) runs Inferpal: the editor starts the agent as a child process,
and nothing else on the network can reach its input and output. Nothing to configure.

## From another computer: carry it over SSH

ACP is a stream of lines, and SSH carries a program's input and output across machines with authentication and
encryption. Give your client `ssh` as the agent's command, and the agent's own command as its arguments:

```json
{
  "agent_servers": {
    "Inferpal (workstation)": {
      "type": "custom",
      "command": "ssh",
      "args": ["-T", "-o", "BatchMode=yes", "you@workstation", "/opt/inferpal/Inferpal.Host", "--acp"],
      "env": {}
    }
  }
}
```

(Zed's `settings.json`; the JetBrains `acp.json` takes the same `command` and `args`. `env` there sets variables for
`ssh`, not for the agent.)

- Use **key authentication**: the client cannot type a password for you (`BatchMode=yes` makes SSH fail instead of
  asking). A key with a passphrase needs an SSH agent. Connect once by hand first, so that the machine's host key is
  already known.
- The agent works **on the files of the machine it runs on**, in the folder your client names — and your client names
  it as **its own** machine does. Both must name the folder the same way: the agent refuses a folder it does not have,
  and a path written the other system's way — a Windows client's `C:\…` when it runs on Linux or macOS, a Linux or
  macOS client's `/home/…` when it runs on Windows — and says which.
- Set Inferpal up **on that machine**, in a terminal there: `Inferpal.Host --acp --setup` looks for a model server
  there, then asks for its address and a model. The settings window (`Inferpal.Host --settings`, your client's settings button, `/settings`
  in a thread) and the sign-in page of an MCP server open in **that machine's** browser, not yours.

Measured with a script driving the agent through the official ACP client library, on Windows, and the agent on Linux
(WSL) behind a key-only SSH server, the paths named as the agent's machine names them: a question, and an edit of two
files with each diff shown at the approval and the files on disk equal to it, behave exactly as when the agent is
started directly. Not yet tried from Zed or a JetBrains IDE.

## Phones and browsers: WebSocket bridges

Mobile and web clients cannot start a program; they connect to a WebSocket address, and a bridge on your machine starts
the agent for each connection. Two bridges go by the name some clients recommend — `stdio-to-ws` and
`@rebornix/stdio-to-ws`, the one acp-ui shows (both 0.2.0, read in their source and measured). Both:

- **listen on every network interface** of the machine — measured; their command line has no option to choose the
  address;
- have **no authentication and no encryption**, and **do not check which web page connects**: a page open in a
  browser that can reach the port — on the same machine included — can try to connect; some browsers ask you first,
  the bridge never refuses;
- start **a new agent for every connection**, with your settings, your models and your permissions.

`@rebornix/stdio-to-ws` also keeps the agent running after the connection drops when started with `--persist`
(`--grace-period -1`: until the bridge stops), and prints every message — your files' contents included — to its
console, even when started with `--quiet` (measured).

So, with such a bridge:

- run it only while you use it;
- never on a network you do not control (a café, a hotel, a shared office, a guest network);
- never behind a public or **anonymous tunnel**, and never through a port opened on your router: whoever finds the
  address drives your machine;
- at most inside a private, encrypted network that only your own devices join (a WireGuard or Tailscale network),
  with the machine's firewall closing the bridge's port everywhere else. Inferpal has not measured this setup.

## Your model server is another door

Everything above is about the agent. Your model server (Ollama, LM Studio, llama.cpp) answers on its own port, and a
server listening on every interface without authentication is open to anyone who reaches it: see
[Remote Inference](remote-inference.md#security-note) — a trusted private network only, and TLS with authentication beyond
it.
