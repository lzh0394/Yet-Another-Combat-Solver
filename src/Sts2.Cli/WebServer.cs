using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sts2.Contracts;
using Sts2.Engine.Combat;
using Sts2.Engine.Data;
using Sts2.View;

namespace Sts2.Cli;

/// <summary>
/// Minimal local web UI (M8): the engine is authoritative, the browser only renders snapshots and
/// posts actions. One combat per session id; no persistence, no auth, bound to 127.0.0.1.
/// Implemented on a raw TCP listener because HttpListener needs an http.sys URL reservation.
/// </summary>
internal static class WebServer
{
    private sealed class Session
    {
        public required Combat Combat { get; init; }
        public int Actions { get; set; }
        public List<string> Log { get; } = new();
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, string Body);

    private static readonly ConcurrentDictionary<string, Session> Sessions = new();
    private static DataPack _pack = null!;
    private static Scenario _template = null!;

    public static int Run(int port, string packDir, string scenarioPath)
    {
        _pack = DataPack.Load(packDir);
        _template = JsonSerializer.Deserialize(File.ReadAllText(scenarioPath), PackJsonContext.Default.Scenario)
            ?? throw new InvalidOperationException("scenario is empty");

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"[web] listening on http://127.0.0.1:{port}/");
        Console.WriteLine($"[web] scenario: {Path.GetFileName(scenarioPath)}  (ctrl+c to stop)");
        while (true)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                break;
            }
            ThreadPool.QueueUserWorkItem(_ => Serve(client));
        }
        return 0;
    }

    private static void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                using NetworkStream stream = client.GetStream();
                Request? request = ReadRequest(stream);
                if (request is null)
                {
                    return;
                }
                (int status, string contentType, string body) = Route(request);
                WriteResponse(stream, status, contentType, body);
            }
            catch (Exception ex)
            {
                try
                {
                    WriteResponse(client.GetStream(), 500, JsonType, Error(ex.GetType().Name + ": " + ex.Message));
                }
                catch
                {
                    // client went away
                }
            }
        }
    }

    private static Request? ReadRequest(NetworkStream stream)
    {
        var head = new MemoryStream();
        var one = new byte[1];
        // Head ends with CRLFCRLF; read byte-wise and cap it at 32 KiB.
        while (head.Length < 32 * 1024)
        {
            int read = stream.Read(one, 0, 1);
            if (read == 0)
            {
                return null;
            }
            head.WriteByte(one[0]);
            if (head.Length >= 4)
            {
                byte[] buffer = head.GetBuffer();
                int n = (int)head.Length;
                if (buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
                {
                    break;
                }
            }
        }
        string[] lines = Encoding.UTF8.GetString(head.GetBuffer(), 0, (int)head.Length)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }
        string[] parts = lines[0].Split(' ');
        if (parts.Length < 2)
        {
            return null;
        }
        string target = parts[1];
        int q = target.IndexOf('?');
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q >= 0)
        {
            foreach (string pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = pair.Split('=', 2);
                query[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            }
            target = target[..q];
        }
        int length = 0;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line[(colon + 1)..].Trim(), out length);
            }
        }
        var body = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = stream.Read(body, offset, length - offset);
            if (read == 0)
            {
                break;
            }
            offset += read;
        }
        return new Request(parts[0], target, query, offset == 0 ? "" : Encoding.UTF8.GetString(body, 0, offset));
    }

    private static (int Status, string ContentType, string Body) Route(Request request)
    {
        if (request.Method == "GET" && request.Path == "/")
        {
            return (200, "text/html; charset=utf-8", Page);
        }
        if (request.Method == "POST" && request.Path == "/api/new")
        {
            string id = Guid.NewGuid().ToString("N")[..8];
            var combat = new Combat(_pack, _template);
            Sessions[id] = new Session { Combat = combat };
            return (200, JsonType, "{\"session\":\"" + id + "\",\"state\":"
                + JsonSerializer.Serialize(Snapshot(combat), ViewJsonContext.Default.Snapshot) + "}");
        }
        if (!request.Query.TryGetValue("id", out string? sessionId) || !Sessions.TryGetValue(sessionId, out Session? session))
        {
            return (404, JsonType, "{\"error\":\"unknown session\"}");
        }
        switch (request.Method, request.Path)
        {
            case ("GET", "/api/state"):
                return (200, JsonType, PayloadJson(session));
            case ("POST", "/api/play"):
            {
                using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                JsonElement root = doc.RootElement;
                int? slot = root.TryGetProperty("slot", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
                string? target = root.TryGetProperty("target", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                session.Combat.PlayCard(slot, null, target, session.Actions++);
                session.Log.Add($"play #{slot}{(target is null ? "" : " → " + target)}");
                return (200, JsonType, PayloadJson(session));
            }
            case ("POST", "/api/end"):
                session.Combat.EndTurn(session.Actions++);
                session.Log.Add("end turn");
                return (200, JsonType, PayloadJson(session));
            default:
                return (404, JsonType, "{\"error\":\"not found\"}");
        }
    }

    private const string JsonType = "application/json; charset=utf-8";

    private static Snapshot Snapshot(Combat combat) => Snapshots.Build(combat, new ActionInfo { Type = "state" });

    private static string PayloadJson(Session session)
    {
        string log = "[" + string.Join(",", session.Log.Select(x => JsonSerializer.Serialize(x))) + "]";
        return "{\"state\":" + JsonSerializer.Serialize(Snapshot(session.Combat), ViewJsonContext.Default.Snapshot)
            + ",\"log\":" + log
            + ",\"over\":" + (session.Combat.CombatOver ? "true" : "false") + "}";
    }

    private static string Error(string message)
        => "{\"error\":" + JsonSerializer.Serialize(message) + "}";

    private static void WriteResponse(Stream stream, int status, string contentType, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", _ => "Error" };
        string head = $"HTTP/1.1 {status} {reason}\r\n"
            + $"Content-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        stream.Write(headBytes);
        stream.Write(payload);
        stream.Flush();
    }

    private const string Page = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>STS2 engine — local play</title>
<style>
body{margin:0;font:14px/1.5 ui-sans-serif,system-ui,"Segoe UI",sans-serif;background:#12141a;color:#e6e8ef}
header{padding:14px 20px;border-bottom:1px solid #262a35;display:flex;gap:12px;align-items:center}
h1{font-size:16px;margin:0;flex:1}
main{display:grid;grid-template-columns:1.1fr 1fr;gap:16px;padding:16px 20px}
.panel{background:#191d27;border:1px solid #262a35;border-radius:10px;padding:12px 14px}
h2{font-size:13px;text-transform:uppercase;letter-spacing:.04em;color:#8f9ab3;margin:0 0 8px}
button{background:#2d3346;color:#e6e8ef;border:1px solid #3b435a;border-radius:8px;padding:6px 12px;cursor:pointer}
button:hover{border-color:#6b7896}
button:disabled{opacity:.4;cursor:not-allowed}
.card{display:inline-block;background:#232838;border:1px solid #333a4d;border-radius:8px;padding:6px 8px;margin:0 8px 8px 0;cursor:pointer;min-width:96px;text-align:left}
.card:hover{border-color:#7d8bb0}
.card .cost{background:#f0c040;color:#1b1b1b;border-radius:50%;padding:0 7px;margin-right:6px;font-size:12px;font-weight:700}
.card.unplayable{opacity:.45;cursor:not-allowed}
.enemy{border-left:3px solid #c0392b;padding-left:10px;margin-bottom:10px}
.bar{height:8px;border-radius:4px;background:#2a3040;overflow:hidden;margin:4px 0}
.bar>i{display:block;height:100%;background:linear-gradient(90deg,#c0392b,#e74c3c)}
.intent{color:#ffb35c;font-size:12px}
ol{padding-left:18px;color:#a9b1c4;font-size:12px;max-height:160px;overflow:auto}
#msg{color:#ff9b9b;min-height:18px;font-size:12px}
.muted{color:#8f9ab3;font-size:12px}
</style>
</head>
<body>
<header><h1>Slay the Spire 2 — engine play (local)</h1><button id="new">new combat</button></header>
<main>
  <section class="panel" id="left"></section>
  <section class="panel" id="right"></section>
</main>
<script>
let id = null, state = null, log = [];
const $ = (x) => document.getElementById(x);
const title = (s) => (s || "").replace(/^[A-Z]+\./, "").split("_").map(w => w.charAt(0) + w.slice(1).toLowerCase()).join(" ");
async function post(path, body) {
  $("msg").textContent = "";
  const url = path + (path === "/api/new" ? "" : "?id=" + id);
  const res = await fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body || {}) });
  const data = await res.json();
  if (data.error) { $("msg").textContent = data.error; return; }
  if (data.session) id = data.session;
  apply(data);
}
function apply(data) {
  state = data.state;
  if (data.log) log = data.log;
  render();
}
function render() {
  const s = state, p = s.player;
  const pct = p.max_hp ? 100 * p.hp / p.max_hp : 0;
  const intents = (list) => list.map(x => x.type === "Attack" ? `${x.damage}${x.repeats > 1 ? "×" + x.repeats : ""}` : x.type.toLowerCase()).join(" + ");
  $("left").innerHTML = `<h2>${title(p.id)}</h2>`
    + `<div class="muted">HP ${p.hp}/${p.max_hp} · block ${p.block} · energy ${p.energy}/${p.max_energy} · round ${s.round}</div>`
    + `<div class="bar"><i style="width:${pct}%"></i></div>`
    + `<div class="muted">${p.powers.map(x => title(x.id) + " " + x.amount).join(", ") || "no powers"}</div>`
    + `<h2 style="margin-top:12px">Hand</h2>`
    + s.piles.hand.map((c, i) => `<span class="card ${c.can_play === false ? "unplayable" : ""}" data-slot="${i}">`
      + `<span class="cost">${c.costs_x ? "X" : c.cost}</span>${title(c.id)}${c.upgrade ? "+" : ""}<br><small class="muted">${c.target || ""}</small></span>`).join("")
    + `<div class="muted">draw ${s.piles.draw.length} · discard ${s.piles.discard.length} · exhaust ${s.piles.exhaust.length}</div>`
    + `<div style="margin-top:10px"><button id="end" ${s.combat_over ? "disabled" : ""}>end turn</button></div>`;
  $("right").innerHTML = `<h2>Enemies</h2>` + s.enemies.map(e =>
      `<div class="enemy"><b>${title(e.id)}</b> ${e.hp}/${e.max_hp}${e.block ? " · block " + e.block : ""}`
      + `<div class="bar"><i style="width:${e.max_hp ? 100 * e.hp / e.max_hp : 0}%"></i></div>`
      + `<div class="intent">intent: ${intents(e.intents) || "—"}</div></div>`).join("")
    + (s.combat_over ? `<div class="muted">combat over</div>` : "")
    + `<h2 style="margin-top:12px">Log</h2><ol>` + log.map(x => `<li>${x}</li>`).join("") + `</ol><div id="msg"></div>`;
  document.querySelectorAll(".card[data-slot]").forEach(el => {
    el.onclick = () => el.classList.contains("unplayable") ? null : post("/api/play", { slot: Number(el.dataset.slot), target: "0" });
  });
  $("end").onclick = () => post("/api/end", {});
}
$("new").onclick = () => post("/api/new", {});
post("/api/new", {});
</script>
</body>
</html>
""";
}
