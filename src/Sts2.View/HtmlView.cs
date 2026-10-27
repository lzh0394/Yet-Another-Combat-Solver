using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sts2.Contracts;

namespace Sts2.View;

/// <summary>
/// Renders a recorded parity transcript (one <see cref="Snapshot"/> per line) into a single
/// self-contained HTML file: the JSON is embedded, there are no external assets and no build step.
/// The viewer is read-only; it replays what the engine or the real game recorded.
/// </summary>
public static class HtmlView
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.Default,
    };

    public static string Render(IReadOnlyList<Snapshot> snapshots, string title)
    {
        string json = JsonSerializer.Serialize(snapshots, JsonOptions)
            .Replace("</", "<\\/", StringComparison.Ordinal)
            .Replace("<!--", "<\\!--", StringComparison.Ordinal);

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>")
          .Append(Escape(title)).Append(" — STS2 parity replay</title>\n<style>\n").Append(Css)
          .Append("</style>\n</head>\n<body>\n<header>\n<h1>").Append(Escape(title)).Append("</h1>\n")
          .Append("<div class=\"meta\">").Append(snapshots.Count)
          .Append(" snapshots · engine and real-game transcripts share this format</div>\n</header>\n")
          .Append("<nav><button id=\"prev\">◀</button><input id=\"seek\" type=\"range\" min=\"0\" max=\"")
          .Append(Math.Max(0, snapshots.Count - 1)).Append("\" value=\"0\"><button id=\"next\">▶</button>")
          .Append("<span id=\"pos\"></span></nav>\n<main>\n")
          .Append("<section id=\"status\" class=\"panel\"></section>\n")
          .Append("<section id=\"enemies\" class=\"panel\"></section>\n")
          .Append("<section id=\"piles\" class=\"panel\"></section>\n")
          .Append("</main>\n<footer>keys: ← → navigate · Home/End jump</footer>\n")
          .Append("<script>\nconst DATA = ").Append(json).Append(";\n").Append(Script).Append("</script>\n</body>\n</html>\n");
        return sb.ToString();
    }

    public static async Task<int> WriteAsync(
        string transcriptPath, string outputPath, string? title = null,
        JsonSerializerOptions? options = null)
    {
        options ??= JsonOptions;
        var snapshots = new List<Snapshot>();
        foreach (string line in await File.ReadAllLinesAsync(transcriptPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            Snapshot? snapshot = JsonSerializer.Deserialize<Snapshot>(line, options);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }
        title ??= Path.GetFileNameWithoutExtension(transcriptPath);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllTextAsync(outputPath, Render(snapshots, title));
        return snapshots.Count;
    }

    private static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text);

    private const string Css = """
body{margin:0;font:14px/1.45 ui-sans-serif,system-ui,"Segoe UI",sans-serif;background:#12141a;color:#e6e8ef}
header{padding:16px 24px;border-bottom:1px solid #262a35}
h1{margin:0 0 4px;font-size:18px}
.meta{color:#8b93a7;font-size:12px}
nav{display:flex;gap:10px;align-items:center;padding:10px 24px;border-bottom:1px solid #262a35;position:sticky;top:0;background:#12141a;z-index:2}
nav button{background:#232838;color:#e6e8ef;border:1px solid #333a4d;border-radius:6px;padding:4px 12px;cursor:pointer}
nav input{flex:1}
#pos{color:#8b93a7;font-variant-numeric:tabular-nums;min-width:64px;text-align:right}
main{display:grid;grid-template-columns:repeat(auto-fit,minmax(340px,1fr));gap:16px;padding:16px 24px}
.panel{background:#191d27;border:1px solid #262a35;border-radius:10px;padding:12px 14px}
.panel h2{margin:0 0 10px;font-size:13px;text-transform:uppercase;letter-spacing:.08em;color:#8b93a7}
.bar{height:8px;border-radius:4px;background:#2a3040;overflow:hidden;margin:4px 0}
.bar>i{display:block;height:100%;background:linear-gradient(90deg,#c0392b,#e74c3c)}
.block{display:inline-block;background:#22405c;color:#9fd3ff;border-radius:4px;padding:0 6px;margin-left:6px;font-size:12px}
.pill{display:inline-block;background:#2a3040;border-radius:999px;padding:1px 8px;margin:2px 4px 2px 0;font-size:12px}
.pill.power{background:#3a2f4d;color:#d5c2ff}
.pill.debuff{background:#4d2f2f;color:#ffc2c2}
.card{display:inline-block;background:#232838;border:1px solid #333a4d;border-radius:8px;padding:6px 8px;margin:0 8px 8px 0;min-width:96px}
.card .name{font-weight:600}
.card .cost{background:#f0c040;color:#1b1b1b;border-radius:50%;padding:0 6px;margin-right:6px;font-size:12px;font-weight:700}
.card .sub{color:#8b93a7;font-size:11px}
.card.unplayable{opacity:.5}
.enemy{border-left:3px solid #c0392b;padding-left:10px;margin-bottom:10px}
.enemy.dead{opacity:.35;border-color:#444}
.intent{color:#ffb35c;font-size:12px}
.piles{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:6px;color:#8b93a7;font-size:12px}
.piles b{color:#e6e8ef;display:block;font-size:15px}
#log{margin:0;padding-left:18px;color:#a9b1c4;font-size:12px;max-height:160px;overflow:auto}
footer{padding:10px 24px;color:#5f677b;font-size:12px;border-top:1px solid #262a35}
""";

    private const string Script = """
const $ = (id) => document.getElementById(id);
let i = 0;
const title = (s) => (s || "").replace(/^[A-Z]+\./, "").split("_").map(w => w.charAt(0) + w.slice(1).toLowerCase()).join(" ");
const powers = (list) => list.length
  ? list.map(p => `<span class="pill ${/WEAK|VULNERABLE|FRAIL|SHRINK|CONFUSED|ENTANGLED|DEBUFF/.test(p.id) ? "debuff" : "power"}" title="${p.id}">${title(p.id)} ${p.amount}</span>`).join("")
  : `<span class="pill">none</span>`;
const intents = (list) => list.map(x => {
  if (x.type === "Attack") return `${x.damage}${x.repeats > 1 ? "×" + x.repeats : ""}`;
  if (x.type === "StatusCard") return "status";
  return x.type.toLowerCase();
}).join(" + ");
const card = (c) => {
  const cost = c.costs_x ? "X" : c.cost;
  const bits = [c.upgrade ? "upgraded" : "", c.target || ""].filter(Boolean).join(" · ");
  return `<span class="card ${c.can_play === false ? "unplayable" : ""}"><span class="cost">${cost}</span>`
    + `<span class="name">${title(c.id)}</span><span class="sub">${bits || " "}</span></span>`;
};
const pile = (name, list) => `<div><b>${name}: ${list.length}</b>${list.length ? list.map(x => title(x.id) + (x.upgrade ? "+" : "")).join(", ") : ""}</div>`;

function render() {
  const s = DATA[i];
  if (!s) return;
  $("pos").textContent = `${i + 1} / ${DATA.length}`;
  $("seek").value = i;
  const p = s.player;
  const pct = p.max_hp ? Math.max(0, Math.min(100, 100 * p.hp / p.max_hp)) : 0;
  $("status").innerHTML = `<h2>Player · round ${s.round} · turn ${s.turn}${s.combat_over ? " · combat over" : ""}</h2>`
    + `<div><b>${title(p.id)}</b> ${p.hp}/${p.max_hp}<span class="block">block ${p.block}</span></div>`
    + `<div class="bar"><i style="width:${pct}%"></i></div>`
    + `<div>energy ${p.energy}/${p.max_energy}${p.stars ? " · stars " + p.stars : ""} · ${s.side} side</div>`
    + `<div style="margin-top:8px">${powers(p.powers)}</div>`
    + `<div style="margin-top:6px">${p.relics.map(r => `<span class="pill" title="${r.id}">${title(r.id)}${r.counter ? " " + r.counter : ""}</span>`).join("") || `<span class="pill">no relics</span>`}</div>`
    + `<div style="margin-top:8px;color:#8b93a7">action: ${actionText(s.action)}</div>`;
  $("enemies").innerHTML = `<h2>Enemies</h2>` + (s.enemies.length ? s.enemies.map(e =>
    `<div class="enemy ${e.alive ? "" : "dead"}"><div><b>${title(e.id)}</b> ${e.hp}/${e.max_hp}${e.block ? `<span class="block">block ${e.block}</span>` : ""}</div>`
    + `<div class="bar"><i style="width:${e.max_hp ? 100 * e.hp / e.max_hp : 0}%"></i></div>`
    + `<div class="intent">intent: ${intents(e.intents) || "—"}</div>`
    + `<div>${powers(e.powers)}</div></div>`).join("") : `<div class="dead">none</div>`);
  const piles = s.piles;
  $("piles").innerHTML = `<h2>Hand</h2><div>${piles.hand.map(card).join("") || "empty"}</div>`
    + `<div class="piles">${pile("Draw", piles.draw)}${pile("Discard", piles.discard)}${pile("Exhaust", piles.exhaust)}${pile("Play", piles.play)}</div>`
    + `<h2 style="margin-top:12px">Actions</h2><ol id="log">${DATA.map((x, n) => `<li style="opacity:${n === i ? 1 : .55}">${actionText(x.action)}</li>`).join("")}</ol>`;
}

function actionText(a) {
  if (!a) return "start";
  if (a.type === "end_turn") return "end turn";
  if (a.type === "auto_turn") return "auto turn";
  if (a.type === "play") return `play ${a.card ? title(a.card) : "#" + a.slot}${a.target ? " → " + a.target : ""}`;
  return a.type;
}

function go(n) { i = Math.max(0, Math.min(DATA.length - 1, n)); render(); }
$("prev").onclick = () => go(i - 1);
$("next").onclick = () => go(i + 1);
$("seek").oninput = (e) => go(Number(e.target.value));
document.addEventListener("keydown", (e) => {
  if (e.key === "ArrowLeft") go(i - 1);
  else if (e.key === "ArrowRight") go(i + 1);
  else if (e.key === "Home") go(0);
  else if (e.key === "End") go(DATA.length - 1);
});
render();
""";
}
